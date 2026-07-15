namespace GCode_Sender
{
    // Overwritten by .github/workflows/release.yml immediately before the CI build (with the commit
    // that triggered it), so the compiled binary can identify exactly which commit it was built from.
    // Local/dev builds keep the "dev" placeholder - Check for Updates treats that as "can't compare".
    internal static class BuildInfo
    {
        public const string CommitSha = "dev";
    }
}
