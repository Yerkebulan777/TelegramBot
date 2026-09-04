namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class RuntimeSettings
    {
        internal const string GetRootPath = @"
            SELECT SettingValue
            FROM RuntimeSettings
            WHERE SettingKey = 'root_path';";

        internal const string UpsertRootPath = @"
            INSERT INTO RuntimeSettings (SettingKey, SettingValue, UpdatedByUserId)
            VALUES ('root_path', @RootPath, @UpdatedByUserId)
            ON CONFLICT (SettingKey) DO UPDATE
            SET SettingValue = EXCLUDED.SettingValue,
                UpdatedByUserId = EXCLUDED.UpdatedByUserId,
                UpdatedAt = NOW();";

        internal const string SeedRootPath = @"
            INSERT INTO RuntimeSettings (SettingKey, SettingValue)
            VALUES ('root_path', @RootPath)
            ON CONFLICT (SettingKey) DO NOTHING;";
    }
}
