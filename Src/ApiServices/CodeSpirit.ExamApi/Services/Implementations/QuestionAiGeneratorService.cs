using CodeSpirit.Core;
using CodeSpirit.ExamApi.Dtos.Question;
using CodeSpirit.ExamApi.Services.Interfaces;
using CodeSpirit.Shared.Dtos.AI;
using CodeSpirit.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpirit.ExamApi.Services.Implementations;

/// <summary>
/// 题目AI生成服务
/// </summary>
public class QuestionAiGeneratorService : BaseAiGeneratorService<AIGenerateQuestionDto, List<CreateQuestionDto>>
{
    private readonly IAIQuestionGeneratorService _aiQuestionGeneratorService;
    private readonly IQuestionService _questionService;
    private readonly ICurrentUser _currentUser;
    private readonly IQuestionValidationService _questionValidationService;

    /// <summary>
    /// 初始化题目AI生成服务
    /// </summary>
    /// <param name="aiTaskService">AI任务服务</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="serviceScopeFactory">服务范围工厂</param>
    /// <param name="aiQuestionGeneratorService">AI题目生成服务</param>
    /// <param name="questionService">题目服务</param>
    /// <param name="currentUser">当前用户</param>
    /// <param name="questionValidationService">题目验证服务</param>
    public QuestionAiGeneratorService(
        IAiTaskService aiTaskService,
        ILogger<QuestionAiGeneratorService> logger,
        IServiceScopeFactory serviceScopeFactory,
        IAIQuestionGeneratorService aiQuestionGeneratorService,
        IQuestionService questionService,
        ICurrentUser currentUser,
        IQuestionValidationService questionValidationService)
        : base(aiTaskService, logger, serviceScopeFactory)
    {
        _aiQuestionGeneratorService = aiQuestionGeneratorService ?? throw new ArgumentNullException(nameof(aiQuestionGeneratorService));
        _questionService = questionService ?? throw new ArgumentNullException(nameof(questionService));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _questionValidationService = questionValidationService ?? throw new ArgumentNullException(nameof(questionValidationService));
    }

    /// <summary>
    /// 重写异步生成方法，确保在Task.Run之前捕获租户上下文
    /// </summary>
    /// <param name="request">生成请求</param>
    /// <returns>任务ID</returns>
    public override async Task<string> GenerateAsync(AIGenerateQuestionDto request)
    {
        // 在Task.Run之前捕获当前的租户上下文
        var capturedTenantId = _currentUser.TenantId;
        var capturedUserId = _currentUser.Id;
        var capturedUserName = _currentUser.UserName;
        
        _logger.LogDebug("捕获租户上下文：TenantId={TenantId}, UserId={UserId}, UserName={UserName}", 
            capturedTenantId, capturedUserId, capturedUserName);

        string taskId = await _aiTaskService.CreateTaskAsync(GetTaskType(), request);
        
        // 在后台执行生成任务，使用独立的服务范围，并传递捕获的上下文
        _ = Task.Run(async () =>
        {
            using var scope = _serviceScopeFactory.CreateScope();
            try
            {
                // 在独立的服务范围中验证租户上下文
                var scopedCurrentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
                _logger.LogDebug("后台任务中的租户上下文：TenantId={TenantId}, UserId={UserId}, UserName={UserName}", 
                    scopedCurrentUser.TenantId, scopedCurrentUser.Id, scopedCurrentUser.UserName);

                await ExecuteGenerationTaskAsyncWithScope(scope.ServiceProvider, taskId, request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI生成任务执行失败：{TaskId}", taskId);
                
                // 使用独立的服务范围来处理失败任务
                try
                {
                    var aiTaskService = scope.ServiceProvider.GetRequiredService<IAiTaskService>();
                    await aiTaskService.FailTaskAsync(taskId, ex.Message);
                }
                catch (Exception failEx)
                {
                    _logger.LogError(failEx, "更新任务失败状态时出错：{TaskId}", taskId);
                }
            }
        });

        return taskId;
    }

    /// <summary>
    /// 重写执行生成任务方法，使用同步的进度更新机制
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <param name="taskId">任务ID</param>
    /// <param name="request">生成请求</param>
    protected override async Task ExecuteGenerationTaskAsyncWithScope(IServiceProvider serviceProvider, string taskId, AIGenerateQuestionDto request)
    {
        // 从独立的服务范围获取所需的服务
        var aiTaskService = serviceProvider.GetRequiredService<IAiTaskService>();
        
        try
        {
            // 步骤 1: 准备阶段
            await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 1, 10, "正在初始化AI生成环境...");
            await aiTaskService.AddTaskLogAsync(taskId, "开始初始化生成环境");
            
            await OnGenerationStartedWithScope(serviceProvider, request);
            await OnTaskStepWithScope(serviceProvider, taskId, 1, "初始化完成");

            // 步骤 2: AI处理中
            await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 2, 30, "AI正在分析您的需求并生成内容...");
            await aiTaskService.AddTaskLogAsync(taskId, "开始AI内容生成");

            // 创建“非阻塞但有序”的进度回调：
            // - 禁止使用 .Wait()/.Result（会造成线程池饥饿甚至死锁）
            // - 通过任务链串行化更新，既不阻塞回调调用方，也尽量保持进度更新顺序
            object progressUpdateSync = new();
            Task progressUpdateChain = Task.CompletedTask;

            void EnqueueProgressUpdate(double progress, string message)
            {
                var actualProgress = 30 + (int)(progress * 50); // 30% + 50% for generation
                _logger.LogInformation("排队更新进度: {Progress} -> {ActualProgress}%, {Message}", progress, actualProgress, message);

                lock (progressUpdateSync)
                {
                    progressUpdateChain = progressUpdateChain
                        .ContinueWith(async _ =>
                        {
                            try
                            {
                                await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 2, actualProgress, message);
                                if (!string.IsNullOrEmpty(message))
                                {
                                    await aiTaskService.AddTaskLogAsync(taskId, message);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "更新任务进度时出错：{TaskId}", taskId);
                            }
                        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                        .Unwrap();
                }
            }

            var result = await DoGenerateAsyncWithScope(serviceProvider, request, EnqueueProgressUpdate);
            await progressUpdateChain;

            // 步骤 3: 结果处理
            await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 3, 85, "正在处理生成结果...");
            await aiTaskService.AddTaskLogAsync(taskId, "开始处理生成结果");

            try
            {
                await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 3, 87, "正在验证生成结果...");
                await OnResultProcessingWithScope(serviceProvider, request, result);
                
                await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 3, 95, "结果处理完成，准备完成任务...");
                await OnTaskStepWithScope(serviceProvider, taskId, 3, "结果处理完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "结果处理阶段发生错误，但任务将继续完成");
                await aiTaskService.AddTaskLogAsync(taskId, $"结果处理警告: {ex.Message}");
                await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 3, 90, "结果处理遇到问题，但任务继续...");
            }

            // 步骤 4: 完成
            await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 4, 97, "正在准备完成任务...");
            _logger.LogInformation("开始任务完成阶段，TaskId: {TaskId}", taskId);
            
            try
            {
                // 设置任务完成阶段的超时时间
                using var completionCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // 2分钟超时
                
                _logger.LogInformation("获取详情页面URL...");
                string? detailUrl = await GetDetailUrlWithScope(serviceProvider, result).WaitAsync(completionCts.Token);
                _logger.LogInformation("详情页面URL: {DetailUrl}", detailUrl);
                
                await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Running, 4, 98, "正在完成任务...");
                _logger.LogInformation("调用CompleteTaskAsync...");
                
                await aiTaskService.CompleteTaskAsync(taskId, result, detailUrl).WaitAsync(completionCts.Token);
                _logger.LogInformation("CompleteTaskAsync调用完成");
                
                _logger.LogInformation("调用OnGenerationCompletedWithScope...");
                await OnGenerationCompletedWithScope(serviceProvider, request, result).WaitAsync(completionCts.Token);
                _logger.LogInformation("OnGenerationCompletedWithScope调用完成");
                
                _logger.LogInformation("任务完成阶段全部完成，TaskId: {TaskId}", taskId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("任务完成阶段超时（2分钟），强制设置为完成状态，TaskId: {TaskId}", taskId);
                try
                {
                    await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Completed, 4, 100, "任务完成（超时）");
                }
                catch (Exception timeoutEx)
                {
                    _logger.LogError(timeoutEx, "超时后强制完成任务也失败了，TaskId: {TaskId}", taskId);
                }
            }
            catch (Exception completionEx)
            {
                _logger.LogError(completionEx, "任务完成阶段发生错误，TaskId: {TaskId}", taskId);
                // 尝试手动设置任务为完成状态
                try
                {
                    await aiTaskService.UpdateTaskStatusAsync(taskId, AiTaskStatus.Completed, 4, 100, "任务完成（有警告）");
                }
                catch (Exception finalEx)
                {
                    _logger.LogError(finalEx, "手动完成任务也失败了，TaskId: {TaskId}", taskId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI生成任务失败：{TaskId}", taskId);
            await aiTaskService.FailTaskAsync(taskId, ex.Message);
            await OnGenerationFailedWithScope(serviceProvider, request, ex);
        }
    }

    /// <summary>
    /// 获取任务类型名称
    /// </summary>
    /// <returns>任务类型</returns>
    protected override string GetTaskType()
    {
        return "QuestionGeneration";
    }

    /// <summary>
    /// 执行具体的AI生成逻辑
    /// </summary>
    /// <param name="request">生成请求</param>
    /// <param name="progressCallback">进度回调</param>
    /// <returns>生成结果</returns>
    protected override async Task<List<CreateQuestionDto>> DoGenerateAsync(AIGenerateQuestionDto request, Action<double, string>? progressCallback = null)
    {
        progressCallback?.Invoke(0.1, "正在分析题目主题...");
        
        progressCallback?.Invoke(0.3, "正在生成题目内容...");
        
        // 创建一个进度通知服务来桥接进度回调
        var progressNotificationService = new ProgressCallbackNotificationService(progressCallback, _logger);
        
        // 调用AI生成服务，传递进度通知服务
        var result = await _aiQuestionGeneratorService.GenerateQuestionsAsync(request, 
            sessionId: Guid.NewGuid().ToString(), 
            notificationService: progressNotificationService);
        
        progressCallback?.Invoke(1.0, "题目生成完成");
        
        return result;
    }

    /// <summary>
    /// 执行具体的AI生成逻辑（使用独立的服务范围）
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <param name="request">生成请求</param>
    /// <param name="progressCallback">进度回调</param>
    /// <returns>生成结果</returns>
    protected override async Task<List<CreateQuestionDto>> DoGenerateAsyncWithScope(IServiceProvider serviceProvider, AIGenerateQuestionDto request, Action<double, string>? progressCallback = null)
    {
        // 从独立的服务范围获取所需的服务
        var aiQuestionGeneratorService = serviceProvider.GetRequiredService<IAIQuestionGeneratorService>();
        
        // 验证租户上下文是否正确设置
        var scopedCurrentUser = serviceProvider.GetRequiredService<ICurrentUser>();
        _logger.LogDebug("题目生成开始，当前租户上下文：TenantId={TenantId}, UserId={UserId}, UserName={UserName}", 
            scopedCurrentUser.TenantId, scopedCurrentUser.Id, scopedCurrentUser.UserName);
        
        _logger.LogInformation("开始执行题目生成，进度回调是否为空: {IsNull}", progressCallback == null);
        
        progressCallback?.Invoke(0.1, "正在分析题目主题...");
        _logger.LogInformation("已调用进度回调: 0.1");
        
        progressCallback?.Invoke(0.3, "正在生成题目内容...");
        _logger.LogInformation("已调用进度回调: 0.3");
        
        // 创建一个进度通知服务来桥接进度回调
        var progressNotificationService = new ProgressCallbackNotificationService(progressCallback, _logger);
        
        // 调用AI生成服务，传递进度通知服务
        _logger.LogInformation("开始调用AI生成服务");
        var result = await aiQuestionGeneratorService.GenerateQuestionsAsync(request, 
            sessionId: Guid.NewGuid().ToString(), 
            notificationService: progressNotificationService);
        _logger.LogInformation("AI生成服务调用完成，生成了 {Count} 个题目", result.Count);
        
        progressCallback?.Invoke(1.0, "题目生成完成");
        _logger.LogInformation("已调用进度回调: 1.0");
        
        return result;
    }

    /// <summary>
    /// 生成开始前的处理
    /// </summary>
    /// <param name="request">生成请求</param>
    protected override async Task OnGenerationStarted(AIGenerateQuestionDto request)
    {
        await base.OnGenerationStarted(request);
        _logger.LogInformation("开始生成题目，主题：{Topic}，题目数量：{Count}，类型：{Type}", 
            request.Topic, request.Count, request.Type);
    }

    /// <summary>
    /// 生成完成后的处理
    /// </summary>
    /// <param name="request">生成请求</param>
    /// <param name="result">生成结果</param>
    protected override async Task OnGenerationCompleted(AIGenerateQuestionDto request, List<CreateQuestionDto> result)
    {
        await base.OnGenerationCompleted(request, result);
        _logger.LogInformation("题目生成完成，共生成 {Count} 个题目", result.Count);
        
        // 验证和修复生成的题目
        if (result.Any())
        {
            _logger.LogInformation("开始验证和修复生成的题目...");
            try
            {
                var validatedQuestions = await _questionValidationService.ValidateAndFixQuestionsAsync(result);
                
                // 替换原始结果
                result.Clear();
                result.AddRange(validatedQuestions);
                
                _logger.LogInformation("题目验证和修复完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证和修复题目时发生错误，将使用原始题目");
            }
        }
    }

    /// <summary>
    /// 结果处理阶段
    /// </summary>
    /// <param name="request">生成请求</param>
    /// <param name="result">生成结果</param>
    protected override async Task OnResultProcessing(AIGenerateQuestionDto request, List<CreateQuestionDto> result)
    {
        await base.OnResultProcessing(request, result);
        
        // 这里可以添加额外的结果处理逻辑
        // 比如自动保存题目到数据库
        if (result.Any())
        {
            _logger.LogInformation("正在处理生成的 {Count} 个题目", result.Count);
            
            // 自动保存题目到数据库
            await SaveQuestionsToDatabase(result);
        }
    }

    /// <summary>
    /// 结果处理阶段（使用独立的服务范围）
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <param name="request">生成请求</param>
    /// <param name="result">生成结果</param>
    protected override async Task OnResultProcessingWithScope(IServiceProvider serviceProvider, AIGenerateQuestionDto request, List<CreateQuestionDto> result)
    {
        _logger.LogInformation("开始结果处理阶段，生成了 {Count} 个题目", result.Count);
        
        await base.OnResultProcessingWithScope(serviceProvider, request, result);
        
        if (result.Any())
        {
            _logger.LogInformation("正在处理生成的 {Count} 个题目", result.Count);
            
            // 在后台异步保存题目到数据库，不阻塞任务完成
            // 使用独立的 Task.Run 确保完全脱离当前的服务范围
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("开始在后台保存题目到数据库...");
                    
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // 5分钟超时
                    await SaveQuestionsToDatabaseWithScope(result).WaitAsync(cts.Token);
                    
                    _logger.LogInformation("题目保存到数据库完成");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("保存题目到数据库超时（5分钟）");
                }
                catch (ObjectDisposedException ex)
                {
                    _logger.LogWarning(ex, "后台保存题目时检测到对象已释放，这通常发生在应用程序关闭时");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "后台保存题目到数据库时发生错误");
                }
            });
            
            // 立即标记为处理完成，不等待数据库保存
            _logger.LogInformation("题目生成结果处理完成，数据库保存在后台进行");
        }
        
        _logger.LogInformation("结果处理阶段完成");
    }

    /// <summary>
    /// 保存题目到数据库
    /// </summary>
    /// <param name="questions">生成的题目列表</param>
    private async Task SaveQuestionsToDatabase(List<CreateQuestionDto> questions)
    {
        int successCount = 0;
        List<string> failedItems = new();

        foreach (var question in questions)
        {
            try
            {
                await _questionService.CreateQuestionAsync(question);
                successCount++;
                _logger.LogDebug("成功保存题目: {Content}", question.Content.Substring(0, Math.Min(50, question.Content.Length)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存题目失败: {Content}", question.Content.Substring(0, Math.Min(50, question.Content.Length)));
                failedItems.Add($"题目 [{question.Content.Substring(0, Math.Min(30, question.Content.Length))}...] 保存失败: {ex.Message}");
            }
        }

        _logger.LogInformation("题目保存完成: 成功 {SuccessCount} 个，失败 {FailedCount} 个", successCount, failedItems.Count);
        
        if (failedItems.Any())
        {
            _logger.LogWarning("以下题目保存失败: {FailedItems}", string.Join("; ", failedItems));
        }
    }

    /// <summary>
    /// 使用独立的服务范围保存题目到数据库
    /// </summary>
    /// <param name="questions">生成的题目列表</param>
    private async Task SaveQuestionsToDatabaseWithScope(List<CreateQuestionDto> questions)
    {
        _logger.LogInformation("开始保存 {Count} 个题目到数据库", questions.Count);
        
        try
        {
            // 创建新的服务范围，确保 DbContext 不会被释放
            using var scope = _serviceScopeFactory.CreateScope();
            
            // 从新创建的服务范围获取题目服务
            var questionService = scope.ServiceProvider.GetRequiredService<IQuestionService>();
            _logger.LogInformation("成功获取题目服务");
            
            int successCount = 0;
            List<string> failedItems = new();

            for (int i = 0; i < questions.Count; i++)
            {
                var question = questions[i];
                try
                {
                    _logger.LogDebug("正在保存第 {Index}/{Total} 个题目: {Content}", 
                        i + 1, questions.Count, question.Content.Substring(0, Math.Min(50, question.Content.Length)));
                    
                    await questionService.CreateQuestionAsync(question);
                    successCount++;
                    
                    _logger.LogDebug("成功保存第 {Index}/{Total} 个题目", i + 1, questions.Count);
                }
                catch (ObjectDisposedException ex)
                {
                    _logger.LogWarning(ex, "保存第 {Index}/{Total} 个题目时检测到对象已释放: {Content}", 
                        i + 1, questions.Count, question.Content.Substring(0, Math.Min(50, question.Content.Length)));
                    failedItems.Add($"题目 [{question.Content.Substring(0, Math.Min(30, question.Content.Length))}...] 保存失败: 数据库连接已释放");
                    
                    // 如果是 DbContext 已释放，停止继续保存
                    _logger.LogWarning("检测到数据库上下文已释放，停止保存剩余题目");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存第 {Index}/{Total} 个题目失败: {Content}", 
                        i + 1, questions.Count, question.Content.Substring(0, Math.Min(50, question.Content.Length)));
                    failedItems.Add($"题目 [{question.Content.Substring(0, Math.Min(30, question.Content.Length))}...] 保存失败: {ex.Message}");
                }
                
                // 每保存5个题目记录一次进度
                if ((i + 1) % 5 == 0 || i == questions.Count - 1)
                {
                    _logger.LogInformation("保存进度: {Current}/{Total} ({Percentage:F1}%)", 
                        i + 1, questions.Count, (double)(i + 1) / questions.Count * 100);
                }
            }

            _logger.LogInformation("题目保存完成: 成功 {SuccessCount} 个，失败 {FailedCount} 个", successCount, failedItems.Count);
            
            if (failedItems.Any())
            {
                _logger.LogWarning("以下题目保存失败: {FailedItems}", string.Join("; ", failedItems));
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex, "创建服务范围时检测到对象已释放，这通常发生在应用程序关闭时");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存题目到数据库时发生严重错误");
            throw; // 重新抛出异常，让上层处理
        }
    }

    /// <summary>
    /// 获取详情页面URL
    /// </summary>
    /// <param name="result">生成结果</param>
    /// <returns>详情页面URL</returns>
    protected override Task<string?> GetDetailUrl(List<CreateQuestionDto> result)
    {
        // 返回题目管理页面，可以添加筛选条件显示最新生成的题目
        return Task.FromResult<string?>("/exam/Questions");
    }

    /// <summary>
    /// 获取详情页面URL（使用独立的服务范围）
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <param name="result">生成结果</param>
    /// <returns>详情页面URL</returns>
    protected override async Task<string?> GetDetailUrlWithScope(IServiceProvider serviceProvider, List<CreateQuestionDto> result)
    {
        // 可以在这里添加更复杂的逻辑，比如获取保存成功的题目ID等
        return await GetDetailUrl(result);
    }
}

/// <summary>
/// 进度回调通知服务，用于桥接Action回调和IGeneratorNotificationService
/// </summary>
internal class ProgressCallbackNotificationService : IGeneratorNotificationService
{
    private readonly Action<double, string>? _progressCallback;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _progressSemaphore = new(1, 1);

    /// <summary>
    /// 初始化进度回调通知服务
    /// </summary>
    /// <param name="progressCallback">进度回调</param>
    /// <param name="logger">日志记录器</param>
    public ProgressCallbackNotificationService(Action<double, string>? progressCallback, ILogger logger)
    {
        _progressCallback = progressCallback;
        _logger = logger;
    }

    /// <summary>
    /// 发送题目生成开始通知
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="request">生成请求</param>
    public Task NotifyGenerationStartedAsync(string sessionId, AIGenerateQuestionDto request)
    {
        _progressCallback?.Invoke(0.1, "开始生成题目...");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 发送题目生成进度通知
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="stage">当前阶段</param>
    /// <param name="message">消息</param>
    /// <param name="percentage">完成百分比</param>
    public async Task NotifyGenerationProgressAsync(string sessionId, string stage, string message, int percentage)
    {
        await _progressSemaphore.WaitAsync();
        try
        {
            // 将百分比转换为0-1的进度值
            double progress = percentage / 100.0;
            _logger.LogInformation("桥接服务收到进度更新: {Stage} - {Message} ({Percentage}%) -> 转换为进度值: {Progress}", 
                stage, message, percentage, progress);
            
            // 同步调用进度回调，确保进度更新能够立即执行
            _progressCallback?.Invoke(progress, message);
            
            _logger.LogInformation("进度回调已调用: {Progress}, {Message}", progress, message);
            
            // 添加一个小延迟，确保进度更新有时间被处理
            await Task.Delay(100);
        }
        finally
        {
            _progressSemaphore.Release();
        }
    }

    /// <summary>
    /// 发送题目生成完成通知
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="questions">生成的题目</param>
    /// <param name="duration">耗时(毫秒)</param>
    public Task NotifyGenerationCompletedAsync(string sessionId, List<CreateQuestionDto> questions, long duration)
    {
        _progressCallback?.Invoke(1.0, $"题目生成完成，共生成 {questions.Count} 道题目");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 发送题目生成错误通知
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="error">错误信息</param>
    public Task NotifyGenerationErrorAsync(string sessionId, string error)
    {
        _progressCallback?.Invoke(0.0, $"生成失败: {error}");
        return Task.CompletedTask;
    }
}
