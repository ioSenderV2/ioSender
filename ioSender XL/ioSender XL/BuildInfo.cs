namespace GCode_Sender
{
    // Overwritten immediately before the CI build so the compiled binary can identify exactly which
    // commit/version it was built from - by .github/workflows/release.yml for a real release (the
    // version tools/cut-release.ps1 computed for that push), and by beta-release.yml for a PR beta
    // ("<legacyVersion>.beta-pr<N>", e.g. 2.42.beta-pr20, which is what puts the beta's identity in
    // the window title). Local/dev builds keep the "dev" placeholders - Check for Updates treats
    // CommitSha == "dev" as "can't compare".
    //
    // BOTH workflows locate the two constants below by a regex on their exact declaration text,
    // placeholder included, and -replace rewrites EVERY match in the file - so do not restate that
    // text anywhere here (a comment quoting it verbatim gets stamped too), and do not reformat or
    // rename the declarations without updating release.yml and beta-release.yml together.
    internal static class BuildInfo
    {
        public const string CommitSha = "dev";
        public const string Version = "dev";
    }
}
