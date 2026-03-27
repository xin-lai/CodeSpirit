// 文件路径: Utilities/PasswordGenerator.cs
using System.Security.Cryptography;

namespace CodeSpirit.IdentityApi.Utilities
{
    /// <summary>
    /// 密码生成工具
    /// </summary>
    public static class PasswordGenerator
    {
        /// <summary>
        /// 生成随机密码
        /// </summary>
        /// <param name="length">密码长度（默认12位）</param>
        /// <returns>随机生成的密码字符串</returns>
        public static string GenerateRandomPassword(int length = 12)
        {
            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lower = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string special = "!@#$%^&*()-_=+[]{}|;:,.<>?";

            string allChars = upper + lower + digits + special;
            if (length < 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    length,
                    "密码长度不能小于 4（Password length must be at least 4）。");
            }

            // 确保密码包含至少一个大写字母、小写字母、数字和特殊字符
            char[] password = new char[length];
            password[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
            password[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
            password[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
            password[3] = special[RandomNumberGenerator.GetInt32(special.Length)];

            for (int i = 4; i < length; i++)
            {
                password[i] = allChars[RandomNumberGenerator.GetInt32(allChars.Length)];
            }

            // 使用 Fisher–Yates 洗牌打乱字符顺序（避免 Random 可预测性）
            for (int i = password.Length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (password[i], password[j]) = (password[j], password[i]);
            }

            return new string(password);
        }
    }
}
