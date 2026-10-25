using System;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// PlayMode tests never talk to the user's real backend. Live-sync tests run only when
    /// PLANNERDNA_TEST_BACKEND_URL points at a throwaway backend (its own temporary
    /// PLANNERDNA_DATA_DIR); every other test uses a loopback port where nothing listens.
    /// </summary>
    public static class TestBackend
    {
        public const string EnvVar = "PLANNERDNA_TEST_BACKEND_URL";
        public const string DeadPortUrl = "http://127.0.0.1:9";

        public static string Url => Environment.GetEnvironmentVariable(EnvVar);
        public static bool Configured => !string.IsNullOrEmpty(Url);
        public static string UrlOrDeadPort => Configured ? Url : DeadPortUrl;

        public const string IgnoreReason =
            "Set " + EnvVar + " to a throwaway backend (e.g. http://127.0.0.1:8766 started with a temporary " +
            "PLANNERDNA_DATA_DIR) to run live-sync tests. The real backend is never used by tests.";
    }
}
