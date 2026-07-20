namespace GCode_Sender
{
    // Overwritten by .github/workflows/release.yml immediately before the CI build (with the commit
    // that triggered it and the version tools/cut-release.ps1 computed for this push), so the compiled
    // binary can identify exactly which commit/version it was built from. Local/dev builds keep the
    // "dev" placeholders - Check for Updates treats CommitSha == "dev" as "can't compare".
    internal static class BuildInfo
    {
        public const string CommitSha = "dev";
        public const string Version = "dev";
    }
}
