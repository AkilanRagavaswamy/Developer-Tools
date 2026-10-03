using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevTools.Core;

namespace DevTools.Http.Capture;

/// <summary>
/// The root certificate the capture proxy signs with, and the per-host certificates it issues.
/// </summary>
/// <remarks>
/// Reading https bodies means terminating TLS, and terminating TLS means presenting a
/// certificate the client will accept for the host it asked for. That requires a root the
/// machine trusts, which is a real security decision and not something Start should do quietly:
/// <see cref="Create"/> makes the root, <see cref="Trust"/> installs it, and the two are
/// separate calls so the caller can ask first.
///
/// The root is created once and kept. Its thumbprint is recorded in the app's configuration so
/// the next session finds the same one instead of installing a second root every time.
/// </remarks>
public sealed class CaptureCertificates : IDisposable
{
    /// <summary>The subject name of the root. Distinctive enough to find and remove by hand.</summary>
    public const string RootSubject = "CN=DevTools Capture Root, O=DevTools, OU=API Profiler";

    private readonly ConcurrentDictionary<string, X509Certificate2> _leaves = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private CaptureCertificates(X509Certificate2 root) => Root = root;

    /// <summary>The root, with its private key: the proxy signs each host certificate with it.</summary>
    public X509Certificate2 Root { get; }

    public string Thumbprint => Root.Thumbprint;

    /// <summary>
    /// Finds the root recorded by an earlier session, or <see langword="null"/> when it is gone.
    /// </summary>
    /// <remarks>
    /// A user who removes the certificate from Windows should not have it silently reappear, so
    /// a thumbprint that no longer resolves is treated as "no certificate", not as an error.
    /// </remarks>
    public static CaptureCertificates? Load(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            foreach (var candidate in store.Certificates)
            {
                if (string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) &&
                    candidate.HasPrivateKey)
                {
                    return new CaptureCertificates(candidate);
                }
            }
        }
        catch (Exception)
        {
            // An unreadable store is the same as not having one.
        }

        return null;
    }

    /// <summary>
    /// Creates the root and keeps it in the user's personal store. It is <b>not</b> trusted by
    /// this call — see <see cref="Trust"/>.
    /// </summary>
    public static OperationResult<CaptureCertificates> Create()
    {
        try
        {
            using var key = RSA.Create(2048);

            var request = new CertificateRequest(
                RootSubject,
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            // Five years: long enough not to become a recurring chore, short enough that a
            // forgotten install eventually stops working.
            var now = DateTimeOffset.UtcNow;
            var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

            // Round-tripping through PFX is what attaches a persisted key to the certificate, so
            // it can still be used to sign after the process that made it has gone.
            var exportable = X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(exportable);

            return OperationResult<CaptureCertificates>.Ok(new CaptureCertificates(exportable));
        }
        catch (Exception ex)
        {
            return OperationResult<CaptureCertificates>.Fail($"The capture certificate could not be created: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds the root to the current user's trusted roots. Windows shows its own confirmation
    /// dialog, which is the consent that matters — this call cannot bypass it.
    /// </summary>
    public OperationResult<bool> Trust()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            if (!IsTrusted())
            {
                store.Add(Root);
            }

            return OperationResult<bool>.Ok(true);
        }
        catch (CryptographicException ex)
        {
            // The usual cause is the user saying no to the Windows dialog, which is not an error.
            return OperationResult<bool>.Fail($"The certificate was not trusted: {ex.Message}");
        }
        catch (Exception ex)
        {
            return OperationResult<bool>.Fail($"The certificate could not be trusted: {ex.Message}");
        }
    }

    /// <summary>Removes the root from both stores, so the machine is left as it was found.</summary>
    public OperationResult<bool> Remove()
    {
        var problems = new List<string>();

        RemoveFrom(StoreName.Root);
        RemoveFrom(StoreName.My);

        return problems.Count == 0
            ? OperationResult<bool>.Ok(true)
            : OperationResult<bool>.Fail(string.Join(" ", problems));

        void RemoveFrom(StoreName name)
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);

                foreach (var candidate in store.Certificates)
                {
                    if (string.Equals(candidate.Thumbprint, Root.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        store.Remove(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"The certificate could not be removed from {name}: {ex.Message}");
            }
        }
    }

    /// <summary>Whether Windows currently trusts this root for the signed-in user.</summary>
    public bool IsTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            foreach (var candidate in store.Certificates)
            {
                if (string.Equals(candidate.Thumbprint, Root.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// A certificate for one host, signed by the root and cached for the session.
    /// </summary>
    /// <remarks>
    /// Issuing is not cheap and a single page can open connections to a dozen hosts, so each
    /// host is signed once and reused for the rest of the capture.
    /// </remarks>
    public X509Certificate2 ForHost(string host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _leaves.GetOrAdd(host, Issue);
    }

    private X509Certificate2 Issue(string host)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={host}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            critical: false));

        // Modern clients ignore the common name entirely and check the SAN, so the host has to
        // be in both places for the certificate to be accepted anywhere.
        var alternativeNames = new SubjectAlternativeNameBuilder();

        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            alternativeNames.AddIpAddress(address);
        }
        else
        {
            alternativeNames.AddDnsName(host);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());

        var now = DateTimeOffset.UtcNow;
        using var signed = request.Create(
            Root,
            now.AddDays(-1),
            now.AddYears(1),
            Guid.NewGuid().ToByteArray());

        // The signed certificate carries no private key; pairing it back with the one that made
        // the request is what makes it usable as a server certificate.
        using var withKey = signed.CopyWithPrivateKey(key);

        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var leaf in _leaves.Values)
        {
            leaf.Dispose();
        }

        _leaves.Clear();
        Root.Dispose();
    }
}
