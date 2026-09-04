namespace TelegramBot.Data;

internal static partial class SqlQueries
{
    internal static class RuntimeSettings
    {
        internal const string GetRootPath = @"
            SELECT SettingValue
            FROM RuntimeSettings
            WHERE SettingKey = 'root_path';";

        internal const string GetRootPathAdministratorUserId = @"
            SELECT SettingValue
            FROM RuntimeSettings
            WHERE SettingKey = 'root_path_admin_user_id';";

        internal const string LockRootPathAdministration = @"
            SELECT pg_advisory_xact_lock(hashtext('runtime_settings_root_path_admin'));";

        internal const string InsertRootPathAdministratorUserId = @"
            INSERT INTO RuntimeSettings (SettingKey, SettingValue, UpdatedByUserId)
            VALUES ('root_path_admin_user_id', @UserId::TEXT, @UserId)
            ON CONFLICT (SettingKey) DO NOTHING;";

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

        internal const string GetPendingRootPathChange = @"
            SELECT SettingValue FROM RuntimeSettings
            WHERE SettingKey = 'pending_root_path_change';";

        internal const string GetPendingRootPathChangeForUpdate = @"
            SELECT SettingValue FROM RuntimeSettings
            WHERE SettingKey = 'pending_root_path_change' FOR UPDATE;";

        internal const string UpsertPendingRootPathChange = @"
            INSERT INTO RuntimeSettings (SettingKey, SettingValue)
            VALUES ('pending_root_path_change', @Change)
            ON CONFLICT (SettingKey) DO UPDATE
            SET SettingValue = EXCLUDED.SettingValue, UpdatedByUserId = NULL, UpdatedAt = NOW();";

        internal const string UpdatePendingRootPathChange = @"
            UPDATE RuntimeSettings
            SET SettingValue = @Change, UpdatedByUserId = @UpdatedByUserId, UpdatedAt = NOW()
            WHERE SettingKey = 'pending_root_path_change';";
    }
}
