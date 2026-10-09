using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>
/// Reads &lt;appSettings&gt; and &lt;connectionStrings&gt; from web.config, so every setting can live where an existing
/// IIS-hosted MVC application keeps its configuration. Keys use ':' (or '__') for sections: Turnover:Mode.
/// Connection strings become ConnectionStrings:{name}.
/// </summary>
public sealed class WebConfigSource(string path, bool optional) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new WebConfigProvider(path, optional);
}

public sealed class WebConfigProvider(string path, bool optional) : ConfigurationProvider
{
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            if (!optional) throw new InvalidOperationException($"web.config not found at {path}.");
            Data = data;
            return;
        }
        // DTDs are not processed by XDocument.Load by default; no external entities are resolved.
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException("web.config has no <configuration> element.");
        foreach (var add in root.Element("appSettings")?.Elements("add") ?? [])
        {
            var key = (string?)add.Attribute("key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            data[key.Replace("__", ":")] = (string?)add.Attribute("value");
        }
        foreach (var add in root.Element("connectionStrings")?.Elements("add") ?? [])
        {
            var name = (string?)add.Attribute("name");
            if (!string.IsNullOrWhiteSpace(name)) data[$"ConnectionStrings:{name}"] = (string?)add.Attribute("connectionString");
        }
        Data = data;
    }
}

public sealed class KeyVaultSettings
{
    public bool Enabled { get; set; }
    public string? Uri { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Works, but puts a secret in web.config. Prefer CertificateThumbprint or UseManagedIdentity.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Certificate in LocalMachine\My (then CurrentUser\My) for the app registration.</summary>
    public string? CertificateThumbprint { get; set; }
    public bool UseManagedIdentity { get; set; }
    /// <summary>Only secrets whose name starts with this prefix are loaded; the prefix is removed ("Turnover-").</summary>
    public string? SecretPrefix { get; set; }
    public int ReloadMinutes { get; set; }
}

public static class HostConfiguration
{
    /// <summary>
    /// Final order (later wins): appsettings.json → appsettings.{env}.json → web.config → user secrets / environment
    /// variables / command line → Azure Key Vault. Key Vault is last so secrets there always win.
    /// </summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        var path = builder.Configuration["WebConfigPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "web.config");
        var sources = builder.Configuration.Sources;
        // Before the un-prefixed environment-variable source (the DOTNET_/ASPNETCORE_ host source sits near the top and
        // must not be used as the anchor, or web.config would land below appsettings.json).
        var envIndex = sources.Select((s, i) => (s, i))
            .Where(x => x.s is EnvironmentVariablesConfigurationSource e && string.IsNullOrEmpty(e.Prefix))
            .Select(x => x.i).DefaultIfEmpty(-1).Last();
        sources.Insert(envIndex >= 0 ? envIndex : sources.Count, new WebConfigSource(path, optional: true));
        // Re-read so the inserted source is visible to the code below.
        (builder.Configuration as IConfigurationRoot)?.Reload();

        var kv = builder.Configuration.GetSection("KeyVault").Get<KeyVaultSettings>() ?? new KeyVaultSettings();
        if (!kv.Enabled) return;

        // Fail closed: an enabled vault that is misconfigured stops the app instead of running with missing secrets.
        if (!System.Uri.TryCreate(kv.Uri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != "https")
            throw new InvalidOperationException("KeyVault:Enabled is true but KeyVault:Uri is not an https URL.");
        var credential = Credential(kv);
        var client = new SecretClient(vaultUri, credential);
        builder.Configuration.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
        {
            Manager = new PrefixSecretManager(kv.SecretPrefix),
            ReloadInterval = kv.ReloadMinutes > 0 ? TimeSpan.FromMinutes(kv.ReloadMinutes) : null,
        });
    }

    private static Azure.Core.TokenCredential Credential(KeyVaultSettings kv)
    {
        if (kv.UseManagedIdentity)
            return string.IsNullOrWhiteSpace(kv.ClientId) ? new ManagedIdentityCredential() : new ManagedIdentityCredential(kv.ClientId);
        if (string.IsNullOrWhiteSpace(kv.TenantId) || string.IsNullOrWhiteSpace(kv.ClientId))
            throw new InvalidOperationException("KeyVault: set TenantId and ClientId (or UseManagedIdentity=true).");
        if (!string.IsNullOrWhiteSpace(kv.CertificateThumbprint))
            return new ClientCertificateCredential(kv.TenantId, kv.ClientId, FindCertificate(kv.CertificateThumbprint));
        if (!string.IsNullOrWhiteSpace(kv.ClientSecret))
            return new ClientSecretCredential(kv.TenantId, kv.ClientId, kv.ClientSecret);
        throw new InvalidOperationException("KeyVault: set CertificateThumbprint, ClientSecret or UseManagedIdentity.");
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        var clean = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray());
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, clean, validOnly: false);
            if (found.Count > 0) return found[0];
        }
        throw new InvalidOperationException($"KeyVault: certificate {clean} not found in LocalMachine\\My or CurrentUser\\My.");
    }
}

/// <summary>Key Vault names cannot contain ':'; "ConnectionStrings--Db2Qa" becomes ConnectionStrings:Db2Qa.</summary>
public sealed class PrefixSecretManager(string? prefix) : KeyVaultSecretManager
{
    public override bool Load(SecretProperties secret) =>
        string.IsNullOrEmpty(prefix) || secret.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    public override string GetKey(KeyVaultSecret secret) =>
        (string.IsNullOrEmpty(prefix) ? secret.Name : secret.Name[prefix.Length..]).Replace("--", ConfigurationPath.KeyDelimiter);
}
