using System;

namespace ThreeUnity.Bridge
{
    /// <summary>
    /// Opaque capability for exactly one physical WebView page and pipe. Keep the
    /// lease with any asynchronous work that may reply later; a lease from a
    /// retired page is rejected instead of being rebound to the replacement page.
    /// Launcher transport APIs are main-thread APIs.
    /// </summary>
    public sealed class ThreeUnityWebBridgeLease
    {
        private readonly object issuer;
        private readonly object connectionIdentity;

        internal ThreeUnityWebBridgeLease(
            object issuer,
            object connectionIdentity,
            long pageGeneration,
            long connectionGeneration)
        {
            this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            this.connectionIdentity = connectionIdentity
                ?? throw new ArgumentNullException(nameof(connectionIdentity));
            if (pageGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageGeneration));
            if (connectionGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
            PageGeneration = pageGeneration;
            ConnectionGeneration = connectionGeneration;
        }

        public long PageGeneration { get; }
        public long ConnectionGeneration { get; }

        internal bool Matches(
            object candidateIssuer,
            object candidateConnectionIdentity,
            long candidatePageGeneration,
            long candidateConnectionGeneration)
        {
            return ReferenceEquals(issuer, candidateIssuer)
                && ReferenceEquals(connectionIdentity, candidateConnectionIdentity)
                && PageGeneration == candidatePageGeneration
                && ConnectionGeneration == candidateConnectionGeneration;
        }

        public override string ToString()
        {
            return "ThreeUnityWebBridgeLease(page="
                + PageGeneration
                + ", connection="
                + ConnectionGeneration
                + ")";
        }
    }
}
