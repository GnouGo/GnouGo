namespace GnOuGo.Agent.Mcp.Data;

internal static class AgentSqliteSchema
{
    public const string CreateAgentsTable =
        """
        CREATE TABLE IF NOT EXISTS "Agents" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Agents" PRIMARY KEY,
            "TenantId" TEXT NULL,
            "Name" TEXT NOT NULL,
            "Workflow" TEXT NOT NULL,
            "OriginalPrompt" TEXT NULL,
            "ScheduleDescription" TEXT NULL,
            "SchedulesJson" TEXT NOT NULL DEFAULT '[]',
            "CreatedAtTicks" INTEGER NOT NULL,
            "UpdatedAtTicks" INTEGER NOT NULL
        );
        """;

    public const string CreateAgentsTenantIndex =
        "CREATE INDEX IF NOT EXISTS \"IX_Agents_TenantId\" ON \"Agents\" (\"TenantId\");";

    public const string CreateAgentsNameTenantIndex =
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Agents_Name_TenantId\" ON \"Agents\" (\"Name\", \"TenantId\");";

    public const string CreateUserConfigsTable =
        """
        CREATE TABLE IF NOT EXISTS "UserConfigs" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserConfigs" PRIMARY KEY,
            "TenantId" TEXT NULL,
            "TenantScopeKey" TEXT NOT NULL,
            "DefaultLlmProvider" TEXT NULL,
            "DefaultLlmModel" TEXT NULL,
            "DefaultEmbeddingConfig" TEXT NULL,
            "DefaultAgent" TEXT NULL,
            "ModelOverridesJson" TEXT NULL,
            "UpdatedAtTicks" INTEGER NOT NULL
        );
        """;

    public const string CreateUserConfigsTenantScopeIndex =
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserConfigs_TenantScopeKey\" ON \"UserConfigs\" (\"TenantScopeKey\");";

    public const string CreateUserConfigsTenantIndex =
        "CREATE INDEX IF NOT EXISTS \"IX_UserConfigs_TenantId\" ON \"UserConfigs\" (\"TenantId\");";

    public const string CreateDiffEntriesTable =
        """
        CREATE TABLE IF NOT EXISTS "DiffEntries" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_DiffEntries" PRIMARY KEY,
            "EntityType" TEXT NOT NULL,
            "EntityId" TEXT NOT NULL,
            "TimestampTicks" INTEGER NOT NULL,
            "Author" TEXT NOT NULL,
            "CurrentValue" TEXT NOT NULL,
            "DiffFromPrevious" TEXT NULL,
            "ValueHash" TEXT NOT NULL
        );
        """;

    public const string CreateDiffEntriesEntityTimestampIndex =
        "CREATE INDEX IF NOT EXISTS \"IX_DiffEntries_EntityType_EntityId_TimestampTicks\" ON \"DiffEntries\" (\"EntityType\", \"EntityId\", \"TimestampTicks\");";

    public const string CreateDiffEntriesEntityIndex =
        "CREATE INDEX IF NOT EXISTS \"IX_DiffEntries_EntityType_EntityId\" ON \"DiffEntries\" (\"EntityType\", \"EntityId\");";

    public const string CreateDiffEntriesTimestampIndex =
        "CREATE INDEX IF NOT EXISTS \"IX_DiffEntries_TimestampTicks\" ON \"DiffEntries\" (\"TimestampTicks\");";
}
