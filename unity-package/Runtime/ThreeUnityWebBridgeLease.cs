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
        // These are deliberately standalone tokens, not the launcher or its
        // ConnectionResources. Keeping an old lease must not retain disposed I/O.
        private readonly object issuerToken;
        private readonly object connectionToken;

        internal ThreeUnityWebBridgeLease(
            object issuerToken,
            object connectionToken,
            long pageGeneration,
            long connectionGeneration)
        {
            this.issuerToken = issuerToken ?? throw new ArgumentNullException(nameof(issuerToken));
            this.connectionToken = connectionToken
                ?? throw new ArgumentNullException(nameof(connectionToken));
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
            object candidateIssuerToken,
            object candidateConnectionToken,
            long candidatePageGeneration,
            long candidateConnectionGeneration)
        {
            return ReferenceEquals(issuerToken, candidateIssuerToken)
                && ReferenceEquals(connectionToken, candidateConnectionToken)
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
