using DevTools.Http.Model;
using Windows.Security.Credentials;

namespace DevTools.App.Services;

/// <summary>
/// Secrets held in the Windows credential vault rather than in any file DevTools writes
/// (FR-A30, D8).
/// </summary>
/// <remarks>
/// <para>
/// A collection file stores only a reference — <c>auth:&lt;request id&gt;</c> — and the value it
/// points at lives here, encrypted by the OS under the user's account. That is the difference
/// between exporting a collection being safe and it being a credential leak.
/// </para>
/// <para>
/// Every call is wrapped: <see cref="PasswordVault"/> throws a bare <see cref="Exception"/>
/// (HRESULT 0x80070490) for "not found" rather than returning null, and it is unavailable
/// altogether in an unpackaged process. Neither case should stop the app, so both degrade to
/// an in-memory store for the session with the user told once.
/// </para>
/// </remarks>
public sealed class CredentialVaultStore : ICredentialStore
{
    private const string ResourceName = "ForgeKitRk";

    private readonly Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private PasswordVault? _vault;
    private bool _vaultUnavailable;

    /// <summary>True when the OS vault could not be used and secrets are session-only.</summary>
    public bool IsSessionOnly => _vaultUnavailable;

    private PasswordVault? Vault
    {
        get
        {
            if (_vaultUnavailable)
            {
                return null;
            }

            if (_vault is not null)
            {
                return _vault;
            }

            try
            {
                _vault = new PasswordVault();
            }
            catch (Exception)
            {
                _vaultUnavailable = true;
            }

            return _vault;
        }
    }

    public ValueTask<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (Vault is { } vault)
        {
            try
            {
                var credential = vault.Retrieve(ResourceName, reference);
                credential.RetrievePassword();
                return ValueTask.FromResult<string?>(credential.Password);
            }
            catch (Exception)
            {
                // Not found, or the vault refused. Fall through to the session store.
            }
        }

        lock (_fallback)
        {
            return ValueTask.FromResult(_fallback.GetValueOrDefault(reference));
        }
    }

    public ValueTask SetAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return ValueTask.CompletedTask;
        }

        if (Vault is { } vault)
        {
            try
            {
                // Replace rather than add: the vault would otherwise keep both.
                RemoveFromVault(vault, reference);
                vault.Add(new PasswordCredential(ResourceName, reference, secret));
                return ValueTask.CompletedTask;
            }
            catch (Exception)
            {
                _vaultUnavailable = true;
            }
        }

        lock (_fallback)
        {
            _fallback[reference] = secret;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (Vault is { } vault)
        {
            RemoveFromVault(vault, reference);
        }

        lock (_fallback)
        {
            _fallback.Remove(reference);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Forgets every secret DevTools has stored. Offered from Settings.</summary>
    public void Clear()
    {
        if (Vault is { } vault)
        {
            try
            {
                foreach (var credential in vault.FindAllByResource(ResourceName))
                {
                    vault.Remove(credential);
                }
            }
            catch (Exception)
            {
                // Nothing stored, or the vault is unavailable — either way there is nothing to do.
            }
        }

        lock (_fallback)
        {
            _fallback.Clear();
        }
    }

    private static void RemoveFromVault(PasswordVault vault, string reference)
    {
        try
        {
            vault.Remove(vault.Retrieve(ResourceName, reference));
        }
        catch (Exception)
        {
            // Nothing stored under that reference.
        }
    }
}
