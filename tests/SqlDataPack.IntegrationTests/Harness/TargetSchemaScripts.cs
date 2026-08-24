namespace SqlDataPack.IntegrationTests.Harness;

/// <summary>
/// Builds import targets out of the fixtures, never out of C# string literals. A target is the source's own
/// current DDL with no rows, optionally with a named ALTER section from narrow-target-variants.sql applied on
/// top -- so a target cannot drift from the source it is supposed to differ from in exactly one way.
/// </summary>
internal static class TargetSchemaScripts {
    public const string VariantsFixture = "narrow-target-variants.sql";

    /// <summary>Section names in <see cref="VariantsFixture"/>, so a typo is a compile error, not a runtime skip.</summary>
    public static class Variants {
        public const string MissingChildTable = nameof(MissingChildTable);
        public const string MissingColumn = nameof(MissingColumn);
        public const string ExtraAllowedColumns = nameof(ExtraAllowedColumns);
        public const string ExtraRequiredColumn = nameof(ExtraRequiredColumn);
        public const string DefaultedNullables = nameof(DefaultedNullables);
        public const string ThirdTableIncompatible = nameof(ThirdTableIncompatible);
        public const string ConstrainedTarget = nameof(ConstrainedTarget);
        public const string TypeDrift = nameof(TypeDrift);
        public const string DatePrecisionCollapse = nameof(DatePrecisionCollapse);
        public const string CollationSwap = nameof(CollationSwap);
        public const string TemporalTargetForPlainSource = nameof(TemporalTargetForPlainSource);
    }

    /// <summary>
    /// Deploys a source fixture's DDL with none of its seed data: the empty target an import writes into.
    /// Pass <paramref name="section"/> as <see langword="null"/> for a fixture with no <c>-- @@SECTION</c> markers.
    /// </summary>
    public static async Task ApplySourceSchemaUnseededAsync(SqlServerFixtureDatabase db, string fixtureFile, string? section = null) {
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadDdl(fixtureFile, section));
    }

    /// <summary>
    /// The source's own unseeded DDL, then the named variant section from narrow-target-variants.sql on top.
    /// Every altered or created table keeps the source table's exact schema.table name -- import matches
    /// package tables to target tables by name and there is no rename mapping in ImportOptions.
    /// </summary>
    public static async Task ApplyTargetVariantAsync(SqlServerFixtureDatabase db, string sourceFixture, string? sourceSection, string variantName) {
        await ApplySourceSchemaUnseededAsync(db, sourceFixture, sourceSection);
        await ApplyVariantAsync(db, variantName);
    }

    /// <summary>
    /// The variant section alone, for a target that needs more than one source fixture deployed first (the
    /// narrow-target-variants database that pairs with both type-vault and core-commerce).
    /// </summary>
    public static async Task ApplyVariantAsync(SqlServerFixtureDatabase db, string variantName) {
        await db.ExecuteSqlAsync(SqlScriptLoader.LoadSection(VariantsFixture, variantName));
    }
}
