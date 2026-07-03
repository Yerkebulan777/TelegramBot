using System.Text;

namespace TelegramBot.Core.Helpers;

/// <summary>
/// Extension methods для StringBuilder.
/// </summary>
public static class StringBuilderExtensions
{
    /// <summary>
    /// Дописывает данные в builder с ограничением размера; выставляет truncated при превышении лимита.
    /// </summary>
    public static void AppendBounded(this StringBuilder builder, string data, ref bool truncated, int maxChars)
    {
        if (truncated)
        {
            return;
        }

        lock (builder)
        {
            if (truncated)
            {
                return;
            }

            if (builder.Length + data.Length + 1 <= maxChars)
            {
                _ = builder.AppendLine(data);
                return;
            }

            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                _ = builder.Append(data.AsSpan(0, Math.Min(remaining, data.Length)));
            }

            truncated = true;
        }
    }
}
