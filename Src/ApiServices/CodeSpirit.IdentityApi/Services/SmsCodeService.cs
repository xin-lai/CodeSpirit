using CodeSpirit.Core.DependencyInjection;
using CodeSpirit.IdentityApi.Dtos.Settings;
using CodeSpirit.IdentityApi.Services.Sms;
using CodeSpirit.Settings.Services.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;

namespace CodeSpirit.IdentityApi.Services;

/// <summary>
/// 短信验证码服务实现
/// </summary>
public class SmsCodeService : ISmsCodeService, IScopedDependency
{
    private readonly ISettingsService _settingsService;
    private readonly IDistributedCache _cache;
    private readonly ILogger<SmsCodeService> _logger;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// 初始化短信验证码服务
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="cache">分布式缓存</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="serviceProvider">服务提供者</param>
    public SmsCodeService(
        ISettingsService settingsService,
        IDistributedCache cache,
        ILogger<SmsCodeService> logger,
        IServiceProvider serviceProvider)
    {
        _settingsService = settingsService;
        _cache = cache;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 获取短信发送器
    /// </summary>
    /// <param name="provider">短信服务提供商</param>
    /// <returns>短信发送器</returns>
    private ISmsSender GetSmsSender(SmsProvider provider)
    {
        return provider switch
        {
            SmsProvider.None => _serviceProvider.GetRequiredService<DevelopmentSmsSender>(),
            SmsProvider.TencentCloud => _serviceProvider.GetRequiredService<TencentCloudSmsSender>(),
            SmsProvider.Aliyun => _serviceProvider.GetRequiredService<AliyunSmsSender>(),
            _ => _serviceProvider.GetRequiredService<DevelopmentSmsSender>()
        };
    }

    /// <summary>
    /// 生成并发送验证码
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="tenantId">租户ID</param>
    /// <returns>是否发送成功</returns>
    public async Task<bool> SendCodeAsync(string phoneNumber, string tenantId)
    {
        try
        {
            // 获取短信设置
            var settings = await GetSmsSettingsAsync(tenantId);
            if (settings == null || !settings.Enabled)
            {
                _logger.LogWarning("短信验证码功能未启用，租户: {TenantId}", tenantId);
                return false;
            }

            // 检查发送频率限制
            var rateLimitKey = GetRateLimitKey(phoneNumber, tenantId);
            var lastSendTime = await _cache.GetStringAsync(rateLimitKey);
            if (!string.IsNullOrEmpty(lastSendTime))
            {
                // 使用 Round-trip 格式与 InvariantCulture，避免不同时区/区域设置导致解析偏差
                var lastSend = DateTimeOffset.Parse(lastSendTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var interval = TimeSpan.FromSeconds(settings.SendIntervalSeconds);
                var elapsed = DateTimeOffset.UtcNow - lastSend;
                if (elapsed < interval)
                {
                    var remainingSeconds = (int)(interval - elapsed).TotalSeconds;
                    _logger.LogWarning("发送验证码过于频繁，手机号: {PhoneNumber}, 还需等待 {RemainingSeconds} 秒", 
                        phoneNumber, remainingSeconds);
                    return false;
                }
            }

            // 生成验证码
            var code = GenerateCode(settings.CodeLength);

            // 存储验证码到缓存
            var cacheKey = GetCacheKey(phoneNumber, tenantId);
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(settings.CodeExpireSeconds)
            };
            await _cache.SetStringAsync(cacheKey, code, cacheOptions);

            // 记录发送时间
            await _cache.SetStringAsync(rateLimitKey, DateTimeOffset.UtcNow.ToString("O"), 
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(settings.SendIntervalSeconds)
                });

            // 发送短信
            var sender = GetSmsSender(settings.Provider);
            var sendResult = await sender.SendAsync(phoneNumber, code, settings);

            if (sendResult)
            {
                _logger.LogInformation("短信验证码发送成功，手机号: {PhoneNumber}, 提供商: {Provider}", 
                    phoneNumber, settings.Provider);
            }
            else
            {
                _logger.LogError("短信验证码发送失败，手机号: {PhoneNumber}, 提供商: {Provider}", 
                    phoneNumber, settings.Provider);
            }

            return sendResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送短信验证码异常，手机号: {PhoneNumber}", phoneNumber);
            return false;
        }
    }

    /// <summary>
    /// 验证验证码
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="code">验证码</param>
    /// <param name="tenantId">租户ID</param>
    /// <returns>是否验证通过</returns>
    public async Task<bool> VerifyCodeAsync(string phoneNumber, string code, string tenantId)
    {
        try
        {
            // 获取短信设置
            var settings = await GetSmsSettingsAsync(tenantId);
            if (settings == null || !settings.Enabled)
            {
                _logger.LogWarning("短信验证码功能未启用，租户: {TenantId}", tenantId);
                return false;
            }

            // 超级验证码检查（开发/测试环境使用）
            if (settings.EnableSuperCode && code == settings.SuperCode)
            {
                _logger.LogWarning("使用超级验证码登录: {PhoneNumber}, 租户: {TenantId}", phoneNumber, tenantId);
                return true;
            }

            // 正常验证码验证
            var cacheKey = GetCacheKey(phoneNumber, tenantId);
            var cachedCode = await _cache.GetStringAsync(cacheKey);

            if (string.IsNullOrEmpty(cachedCode))
            {
                _logger.LogWarning("验证码不存在或已过期，手机号: {PhoneNumber}", phoneNumber);
                return false;
            }

            if (cachedCode != code)
            {
                _logger.LogWarning("验证码错误，手机号: {PhoneNumber}", phoneNumber);
                return false;
            }

            // 验证成功后删除验证码（防止重复使用）
            await _cache.RemoveAsync(cacheKey);

            _logger.LogInformation("验证码验证成功，手机号: {PhoneNumber}", phoneNumber);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证短信验证码异常，手机号: {PhoneNumber}", phoneNumber);
            return false;
        }
    }

    /// <summary>
    /// 获取短信设置
    /// </summary>
    /// <param name="tenantId">租户ID</param>
    /// <returns>短信设置</returns>
    private async Task<SmsSettingsDto?> GetSmsSettingsAsync(string tenantId)
    {
        try
        {
            var settings = await _settingsService.GetTenantSettingAsync<SmsSettingsDto>(tenantId);
            return settings ?? new SmsSettingsDto();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取短信设置失败，租户: {TenantId}", tenantId);
            return new SmsSettingsDto();
        }
    }

    /// <summary>
    /// 生成验证码
    /// </summary>
    /// <param name="length">验证码长度</param>
    /// <returns>验证码</returns>
    private string GenerateCode(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "验证码长度必须大于 0（Code length must be greater than 0）。");
        }

        // 使用加密安全的随机数生成器，避免验证码可预测
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(chars);
    }

    /// <summary>
    /// 获取验证码缓存键
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="tenantId">租户ID</param>
    /// <returns>缓存键</returns>
    private string GetCacheKey(string phoneNumber, string tenantId)
    {
        return $"SmsCode:{tenantId}:{phoneNumber}";
    }

    /// <summary>
    /// 获取发送频率限制缓存键
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="tenantId">租户ID</param>
    /// <returns>缓存键</returns>
    private string GetRateLimitKey(string phoneNumber, string tenantId)
    {
        return $"SmsCode:RateLimit:{tenantId}:{phoneNumber}";
    }
}

