using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SatisfactoryModManager;

internal sealed record DownloadedMod(string FilePath, string Name, string Version);

internal static class FicsitApiClient
{
    private const string ApiBase = "https://api.ficsit.app";
    private const string GraphQlEndpoint = ApiBase + "/v2/query";
    private const string ModPageBase = "https://ficsit.app/mod/";
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<DownloadedMod> DownloadModAsync(string modId, string version, string dataRoot)
    {
        if (!FicsitInstallRequest.IsSafeValue(modId, 100))
            throw new InvalidOperationException("O identificador do mod recebido do site não é válido.");

        if (!string.IsNullOrWhiteSpace(version) && !FicsitInstallRequest.IsSafeValue(version, 100))
            throw new InvalidOperationException("A versão recebida do site não é válida.");

        var diagnostics = new List<string>();
        var resolved = await ResolveDownloadUrlAsync(modId, version, diagnostics).ConfigureAwait(false);
        if (resolved == null)
        {
            var detail = diagnostics.Count == 0 ? string.Empty : " (" + string.Join(" ", diagnostics) + ")";
            throw new InvalidOperationException(
                "O SMR não expôs um link de download automático para esta versão." + detail +
                " Abra a página do mod no ficsit.app e baixe o pacote manualmente.");
        }

        var downloadsRoot = Path.Combine(dataRoot, "Downloads");
        Directory.CreateDirectory(downloadsRoot);

        var baseName = FicsitInstallRequest.SafeFileName(modId + "_" + (string.IsNullOrWhiteSpace(resolved.Version) ? (string.IsNullOrWhiteSpace(version) ? "latest" : version) : resolved.Version));

        var destination = await DownloadFileAsync(resolved.Url, downloadsRoot, baseName).ConfigureAwait(false);
        ValidateArchive(destination);

        return new DownloadedMod(destination, resolved.Name ?? modId, resolved.Version ?? version);
    }

    /// <summary>Resultado de uma verificação de atualização de mod na SMR.</summary>
    public sealed record ModUpdateInfo(string ModId, string? Name, string? LatestVersion, bool UpdateAvailable, string? Error);

    /// <summary>
    /// Consulta a SMR pela versão mais recente publicada do mod e compara com a versão
    /// atualmente instalada (informada pelo chamador). Não baixa nada — só informa se há
    /// uma versão mais nova disponível. Usa a mesma consulta GraphQL "GetVersions" já usada
    /// para resolver a instalação "latest" a partir do protocolo do ficsit.app.
    /// </summary>
    public static async Task<ModUpdateInfo> CheckForUpdateAsync(string modId, string? currentVersion)
    {
        if (!FicsitInstallRequest.IsSafeValue(modId, 100))
            return new ModUpdateInfo(modId, null, null, false, "Identificador de mod inválido para consulta.");

        var diagnostics = new List<string>();
        try
        {
            var latest = await GetLatestVersionLinkAsync(modId, diagnostics).ConfigureAwait(false);
            if (latest == null)
            {
                var detail = diagnostics.Count == 0
                    ? "Não foi possível obter a versão mais recente na SMR."
                    : string.Join(" ", diagnostics);
                return new ModUpdateInfo(modId, null, null, false, detail);
            }

            var updateAvailable = IsNewerVersion(latest.Version, currentVersion);
            return new ModUpdateInfo(modId, latest.Name, latest.Version, updateAvailable, null);
        }
        catch (Exception ex)
        {
            return new ModUpdateInfo(modId, null, null, false, ex.Message);
        }
    }

    /// <summary>URL da página pública do mod no ficsit.app, para abrir no navegador (download manual, changelog, etc.).</summary>
    public static string GetModPageUrl(string modId) => ModPageBase + Uri.EscapeDataString(modId);

    private static bool IsNewerVersion(string? latestVersion, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;
        if (string.IsNullOrWhiteSpace(currentVersion))
            return true; // Sem versão instalada conhecida: sinaliza para o usuário conferir.

        var latestParsed = TryParseSemver(latestVersion);
        var currentParsed = TryParseSemver(currentVersion);
        if (latestParsed != null && currentParsed != null)
            return latestParsed > currentParsed;

        // Não comparável como semver (ex.: versão com sufixo não numérico): trata qualquer
        // texto diferente como possível atualização, em vez de assumir que está em dia.
        return !string.Equals(
            latestVersion.TrimStart('v', 'V'),
            currentVersion.TrimStart('v', 'V'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SatisfactoryModManager/0.4.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.8");
        return client;
    }

    // ------------------------------------------------------------------
    // Resolução do link de download
    //
    // A API pública da SMR (api.ficsit.app/v2/query) tem um esquema GraphQL
    // conhecido e estável, documentado pelas próprias ferramentas oficiais
    // (ficsit-cli / SatisfactoryModManager oficial). O v0.4.2 tentava
    // "adivinhar" esse esquema via introspecção em tempo de execução, o que
    // é frágil e foi a causa do erro "SMR não expôs um link de download".
    //
    // A partir daqui: 1) tenta a consulta GraphQL correta e conhecida;
    // 2) se isso falhar (ex.: a API mudou de novo), cai para a introspecção
    // dinâmica antiga como rede de segurança; 3) por fim, tenta extrair um
    // link direto da página HTML do mod.
    // ------------------------------------------------------------------
    private static async Task<ResolvedDownload?> ResolveDownloadUrlAsync(string modId, string version, List<string> diagnostics)
    {
        try
        {
            var direct = string.IsNullOrWhiteSpace(version)
                ? await GetLatestVersionLinkAsync(modId, diagnostics).ConfigureAwait(false)
                : await GetVersionLinkAsync(modId, version, diagnostics).ConfigureAwait(false);

            if (direct != null)
                return direct;
        }
        catch (Exception ex)
        {
            diagnostics.Add("Falha ao consultar a API GraphQL (consulta direta): " + ex.Message);
        }

        try
        {
            var heuristic = await ResolveViaSchemaIntrospectionAsync(modId, version, diagnostics).ConfigureAwait(false);
            if (heuristic != null)
                return heuristic;
        }
        catch (Exception ex)
        {
            diagnostics.Add("Falha ao consultar a API GraphQL (introspecção): " + ex.Message);
        }

        var fromPage = await ResolveFromModPageAsync(modId, version).ConfigureAwait(false);
        if (fromPage == null)
            diagnostics.Add("A página do mod no ficsit.app também não continha um link de .zip/.smod reconhecível.");
        return fromPage;
    }

    // Consulta a versão exata pedida: getMod(modId) { version(version) { link } }
    // IMPORTANTE: o campo raiz correto da API atual é "getMod", não "mod" — o nome
    // "mod" nunca existiu no schema público (confirmado nos clientes oficiais
    // ficsit-cli e SatisfactoryModManager). Consultar "mod" faz a API responder
    // com erro de validação (HTTP 422 / "Cannot query field"), o que antes era
    // reportado apenas como "SMR não expôs um link de download".
    private static async Task<ResolvedDownload?> GetVersionLinkAsync(string modId, string version, List<string> diagnostics)
    {
        const string query = """
            query GetVersionLink($modId: ModID!, $version: String!) {
              getMod(modId: $modId) {
                id
                name
                version(version: $version) {
                  id
                  version
                  link
                  hash
                }
              }
            }
            """;

        var variables = new Dictionary<string, object?> { ["modId"] = modId, ["version"] = version };
        using var doc = await RunGraphQlAsync(query, variables, diagnostics, "GetVersionLink").ConfigureAwait(false);
        if (doc == null)
            return null;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (!data.TryGetProperty("getMod", out var mod) || mod.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add($"Mod '{modId}' não foi encontrado na SMR.");
            return null;
        }

        var modName = mod.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;

        if (!mod.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add($"A versão '{version}' não foi encontrada para este mod.");
            return null;
        }

        var link = GetString(versionEl, "link");
        var actualVersion = GetString(versionEl, "version") ?? version;

        if (string.IsNullOrWhiteSpace(link))
        {
            diagnostics.Add("A API respondeu, mas o campo 'link' da versão veio vazio.");
            return null;
        }

        return new ResolvedDownload(NormalizeApiLink(link), modName, actualVersion);
    }

    // Sem versão pedida (instalação "latest" a partir do ficsit.app): busca todas as versões e usa a mais recente.
    private static async Task<ResolvedDownload?> GetLatestVersionLinkAsync(string modId, List<string> diagnostics)
    {
        const string query = """
            query GetVersions($modId: ModID!) {
              getMod(modId: $modId) {
                id
                name
                versions {
                  version
                  link
                  hash
                }
              }
            }
            """;

        var variables = new Dictionary<string, object?> { ["modId"] = modId };
        using var doc = await RunGraphQlAsync(query, variables, diagnostics, "GetVersions").ConfigureAwait(false);
        if (doc == null)
            return null;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (!data.TryGetProperty("getMod", out var mod) || mod.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add($"Mod '{modId}' não foi encontrado na SMR.");
            return null;
        }

        var modName = mod.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;

        if (!mod.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
        {
            diagnostics.Add("A API não retornou nenhuma versão publicada para este mod.");
            return null;
        }

        string? bestVersion = null;
        string? bestLink = null;
        Version? bestParsed = null;

        foreach (var item in versions.EnumerateArray())
        {
            var v = GetString(item, "version");
            var link = GetString(item, "link");
            if (string.IsNullOrWhiteSpace(v) || string.IsNullOrWhiteSpace(link))
                continue;

            var parsed = TryParseSemver(v);

            bool shouldReplace;
            if (bestVersion == null)
                shouldReplace = true;
            else if (parsed != null && bestParsed != null)
                shouldReplace = parsed > bestParsed;
            else if (parsed != null)
                shouldReplace = true; // versão nova é comparável, a melhor até agora não era: prefere a comparável.
            else if (bestParsed != null)
                shouldReplace = false; // versão nova não é comparável, a melhor até agora é: mantém a melhor.
            else
                shouldReplace = string.CompareOrdinal(v, bestVersion) > 0; // nenhuma das duas é comparável: compara como texto.

            if (shouldReplace)
            {
                bestParsed = parsed;
                bestVersion = v;
                bestLink = link;
            }
        }

        if (string.IsNullOrWhiteSpace(bestLink) || string.IsNullOrWhiteSpace(bestVersion))
        {
            diagnostics.Add("Nenhuma das versões retornadas tinha um link de download utilizável.");
            return null;
        }

        return new ResolvedDownload(NormalizeApiLink(bestLink), modName, bestVersion);
    }

    private static Version? TryParseSemver(string value)
    {
        var trimmed = value.TrimStart('v', 'V');
        var dashIndex = trimmed.IndexOfAny(['-', '+']);
        if (dashIndex >= 0)
            trimmed = trimmed[..dashIndex];
        return Version.TryParse(trimmed, out var parsed) ? parsed : null;
    }

    private static async Task<JsonDocument?> RunGraphQlAsync(string query, Dictionary<string, object?> variables, List<string> diagnostics, string operationName)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await Http.PostAsync(
            GraphQlEndpoint,
            new StringContent(payload, Encoding.UTF8, "application/json")).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string bodySnippet;
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                bodySnippet = string.IsNullOrWhiteSpace(body) ? string.Empty : " — " + Truncate(body, 200);
            }
            catch
            {
                bodySnippet = string.Empty;
            }

            diagnostics.Add($"{operationName}: a API respondeu HTTP {(int)response.StatusCode}{bodySnippet}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
                .Where(m => !string.IsNullOrWhiteSpace(m));
            diagnostics.Add($"{operationName}: " + string.Join("; ", messages));
            doc.Dispose();
            return null;
        }

        return doc;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // Os links retornados pela SMR costumam ser caminhos relativos à API
    // (ex.: "/v2/download/..."), não URLs absolutas. Também não têm garantia
    // de terminar em ".zip"/".smod" — por isso a extensão final é decidida
    // durante o download (Content-Disposition ou assinatura do arquivo),
    // e não a partir do texto do link.
    private static string NormalizeApiLink(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return link;

        return ApiBase + (link.StartsWith('/') ? link : "/" + link);
    }

    // ------------------------------------------------------------------
    // Rede de segurança: introspecção dinâmica do esquema (comportamento
    // antigo do v0.4.2), usada apenas se a consulta direta acima falhar,
    // por exemplo se a SMR mudar de esquema novamente no futuro.
    // ------------------------------------------------------------------
    private static async Task<ResolvedDownload?> ResolveViaSchemaIntrospectionAsync(string modId, string version, List<string> diagnostics)
    {
        var schema = await GetSchemaAsync().ConfigureAwait(false);
        var candidates = GetRootCandidates(schema).ToList();
        diagnostics.Add($"Introspecção: {candidates.Count} campo(s) candidato(s) na raiz da API.");

        var anyUrlFoundAnywhere = false;
        foreach (var candidate in candidates.Take(10))
        {
            var query = BuildCandidateQuery(schema, candidate, modId, version);
            if (query == null)
                continue;

            using var response = await Http.PostAsync(
                GraphQlEndpoint,
                new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                continue;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                continue;

            var urls = new List<ResolvedDownload>();
            CollectCandidates(document.RootElement, version, modId, urls, depth: 0);
            if (urls.Count > 0)
                anyUrlFoundAnywhere = true;

            var selected = urls
                .OrderByDescending(x => ScoreUrl(x.Url, version, modId))
                .FirstOrDefault(x => ScoreUrl(x.Url, version, modId) > 0);

            if (selected != null)
                return selected;
        }

        diagnostics.Add(anyUrlFoundAnywhere
            ? "Introspecção: a API retornou link(s) de arquivo, mas nenhum bateu com o mod/versão pedidos."
            : "Introspecção: nenhum campo com link de .zip/.smod foi encontrado na estrutura consultada.");
        return null;
    }

    private static async Task<JsonDocument> GetSchemaAsync()
    {
        const string query = "query Schema { __schema { queryType { fields { name args { name type { kind name ofType { kind name ofType { kind name } } } } type { kind name ofType { kind name ofType { kind name } } } } } types { kind name fields { name args { name type { kind name ofType { kind name ofType { kind name } } } } type { kind name ofType { kind name ofType { kind name } } } } } } }";

        using var response = await Http.PostAsync(
            GraphQlEndpoint,
            new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"))
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
    }

    private static IEnumerable<JsonElement> GetRootCandidates(JsonDocument schema)
    {
        if (!schema.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("__schema", out var graph) ||
            !graph.TryGetProperty("queryType", out var queryType) ||
            !queryType.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var field in fields.EnumerateArray()
                     .OrderByDescending(FieldScore)
                     .Where(f => FieldScore(f) > 0))
            yield return field;
    }

    private static int FieldScore(JsonElement field)
    {
        var name = field.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var score = 0;
        if (name.Contains("mod", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (name.Contains("version", StringComparison.OrdinalIgnoreCase)) score += 2;
        if (name.Contains("release", StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    private static string? BuildCandidateQuery(JsonDocument schema, JsonElement field, string modId, string version)
    {
        var fieldName = field.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(fieldName))
            return null;

        var args = field.TryGetProperty("args", out var argArray) && argArray.ValueKind == JsonValueKind.Array
            ? argArray.EnumerateArray().ToList()
            : new List<JsonElement>();

        var argText = new List<string>();
        foreach (var arg in args)
        {
            var argName = arg.GetProperty("name").GetString() ?? string.Empty;
            var type = arg.GetProperty("type");
            var required = IsNonNull(type);
            var baseType = UnwrapType(type);

            if (baseType is not ("String" or "ID"))
            {
                if (required)
                    return null;
                continue;
            }

            var value = argName.Contains("version", StringComparison.OrdinalIgnoreCase)
                ? version
                : modId;

            if (string.IsNullOrWhiteSpace(value) && required)
                return null;

            if (string.IsNullOrWhiteSpace(value))
                continue;

            argText.Add(argName + ":" + JsonSerializer.Serialize(value));
        }

        var returnType = field.GetProperty("type");
        var returnName = UnwrapType(returnType);
        if (string.IsNullOrWhiteSpace(returnName))
            return null;

        var selection = BuildSelection(schema, returnName, 0, new HashSet<string>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(selection))
            return null;

        var argsPart = argText.Count == 0 ? string.Empty : "(" + string.Join(",", argText) + ")";
        return "query { result:" + fieldName + argsPart + selection + " }";
    }

    private static string BuildSelection(JsonDocument schema, string typeName, int depth, HashSet<string> ancestry)
    {
        if (depth > 4 || ancestry.Contains(typeName))
            return string.Empty;

        var field = FindType(schema, typeName);
        if (field == null || !field.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var nextAncestry = new HashSet<string>(ancestry, StringComparer.Ordinal) { typeName };
        var selections = new List<string>();

        foreach (var child in fields.EnumerateArray())
        {
            var childName = child.TryGetProperty("name", out var cn) ? cn.GetString() : null;
            if (string.IsNullOrWhiteSpace(childName) || childName.StartsWith("__", StringComparison.Ordinal))
                continue;

            var childArgsPart = string.Empty;
            if (child.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                var requiredArgs = args.EnumerateArray().Where(IsRequiredArgument).ToList();
                if (requiredArgs.Count > 1)
                    continue; // não sabemos preencher com segurança mais de um argumento obrigatório.

                if (requiredArgs.Count == 1)
                {
                    var requiredArgName = requiredArgs[0].GetProperty("name").GetString() ?? string.Empty;
                    var requiredArgKind = BaseKind(requiredArgs[0].GetProperty("type"));
                    if (requiredArgKind != "INPUT_OBJECT" || string.IsNullOrWhiteSpace(requiredArgName))
                        continue; // só arriscamos um objeto de filtro vazio; outros tipos obrigatórios seguem sendo pulados.

                    childArgsPart = "(" + requiredArgName + ":{})";
                }
            }

            var childType = UnwrapType(child.GetProperty("type"));
            var childKind = BaseKind(child.GetProperty("type"));
            if (childKind is "SCALAR" or "ENUM")
            {
                selections.Add(childName + childArgsPart);
                continue;
            }

            if (childKind != "OBJECT")
                continue;

            var nested = BuildSelection(schema, childType, depth + 1, nextAncestry);
            if (!string.IsNullOrWhiteSpace(nested))
                selections.Add(childName + childArgsPart + nested);
        }

        return selections.Count == 0 ? string.Empty : "{" + string.Join(" ", selections) + "}";
    }

    private static JsonElement? FindType(JsonDocument schema, string typeName)
    {
        if (!schema.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("__schema", out var graph) ||
            !graph.TryGetProperty("types", out var types) ||
            types.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var type in types.EnumerateArray())
        {
            if (type.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), typeName, StringComparison.Ordinal))
                return type;
        }

        return null;
    }

    private static bool IsRequiredArgument(JsonElement arg)
    {
        return arg.TryGetProperty("type", out var type) && IsNonNull(type);
    }

    private static bool IsNonNull(JsonElement type)
    {
        return type.TryGetProperty("kind", out var kind) && kind.GetString() == "NON_NULL";
    }

    private static string UnwrapType(JsonElement type)
    {
        var current = type;
        for (var i = 0; i < 8; i++)
        {
            if (current.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                return name.GetString()!;
            if (!current.TryGetProperty("ofType", out var inner) || inner.ValueKind != JsonValueKind.Object)
                break;
            current = inner;
        }
        return string.Empty;
    }

    private static string BaseKind(JsonElement type)
    {
        var current = type;
        for (var i = 0; i < 8; i++)
        {
            if (current.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                var value = kind.GetString();
                if (value is not ("NON_NULL" or "LIST"))
                    return value ?? string.Empty;
            }
            if (!current.TryGetProperty("ofType", out var inner) || inner.ValueKind != JsonValueKind.Object)
                break;
            current = inner;
        }
        return string.Empty;
    }

    private static void CollectCandidates(JsonElement element, string version, string modId, List<ResolvedDownload> results, int depth)
    {
        if (depth > 8)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (LooksLikeArchiveUrl(value))
                    results.Add(new ResolvedDownload(value!, null, version));
                break;
            case JsonValueKind.Object:
                string? name = null;
                string? itemVersion = null;
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains("name", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        name ??= property.Value.GetString();
                    if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        itemVersion ??= property.Value.GetString();
                }

                foreach (var property in element.EnumerateObject())
                {
                    // Não restringe mais pelo nome do campo (a API usa "link", não "url"/"download"/"file").
                    // Qualquer string que já pareça uma URL de .zip/.smod é candidata, independente do nome do campo.
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var stringValue = property.Value.GetString();
                        if (LooksLikeArchiveUrl(stringValue))
                            results.Add(new ResolvedDownload(stringValue!, name, itemVersion));
                        else if (property.Name.Equals("link", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(stringValue))
                            // Campos "link" que não são URL absoluta terminando em .zip/.smod ainda são
                            // candidatos válidos (a SMR retorna caminhos relativos à API).
                            results.Add(new ResolvedDownload(NormalizeApiLink(stringValue!), name, itemVersion));
                    }
                    CollectCandidates(property.Value, version, modId, results, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectCandidates(item, version, modId, results, depth + 1);
                break;
        }
    }

    private static int ScoreUrl(string url, string version, string modId)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return -1;

        var score = 1;
        if (LooksLikeArchiveUrl(url)) score += 5;
        if (!string.IsNullOrWhiteSpace(version) && url.Contains(version, StringComparison.OrdinalIgnoreCase)) score += 50;
        if (url.Contains(modId, StringComparison.OrdinalIgnoreCase)) score += 20;
        if (url.Contains("download", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (url.Contains("ficsit", StringComparison.OrdinalIgnoreCase)) score += 10;
        return score;
    }

    private static async Task<ResolvedDownload?> ResolveFromModPageAsync(string modId, string version)
    {
        var url = ModPageBase + Uri.EscapeDataString(modId);
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var matches = Regex.Matches(
            html,
            "https?://[^\"'<>\\s\\]]+\\.(?:zip|smod)(?:\\?[^\"'<>\\s\\]]*)?",
            RegexOptions.IgnoreCase);

        var candidates = matches
            .Select(m => m.Value.Replace("\\u0026", "&").Replace("&amp;", "&"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => new ResolvedDownload(x, modId, version))
            .OrderByDescending(x => ScoreUrl(x.Url, version, modId))
            .ToList();

        return candidates.FirstOrDefault(x => ScoreUrl(x.Url, version, modId) > 0);
    }

    private static bool LooksLikeArchiveUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;
        return uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith(".smod", StringComparison.OrdinalIgnoreCase);
    }

    // Baixa o arquivo e só decide o nome/extensão final depois de ver a
    // resposta HTTP (Content-Disposition ou assinatura do arquivo), já que
    // o link da SMR nem sempre termina em ".zip"/".smod" no texto da URL.
    private static async Task<string> DownloadFileAsync(string url, string downloadsRoot, string baseName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("O link de download retornado pelo serviço não é HTTP(S) válido.");

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        const long maximumBytes = 2L * 1024 * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maximumBytes)
            throw new InvalidOperationException("O pacote retornado é maior que o limite seguro de 2 GB.");

        var extension = GuessExtension(response.Content.Headers, uri);
        var destination = Path.Combine(downloadsRoot, baseName + extension);

        await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var output = File.Create(destination))
        {
            await input.CopyToAsync(output).ConfigureAwait(false);
        }

        return destination;
    }

    private static string GuessExtension(HttpContentHeaders headers, Uri requestUri)
    {
        var dispositionFileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(dispositionFileName))
        {
            dispositionFileName = dispositionFileName.Trim('"');
            var ext = Path.GetExtension(dispositionFileName);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".smod", StringComparison.OrdinalIgnoreCase))
                return ext.ToLowerInvariant();
        }

        if (requestUri.AbsolutePath.EndsWith(".smod", StringComparison.OrdinalIgnoreCase))
            return ".smod";
        if (requestUri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return ".zip";

        // Padrão: a maioria dos pacotes da SMR é distribuída como .zip,
        // e ValidateArchive() confirma isso abrindo o arquivo como ZIP
        // (um .smod também é um ZIP válido, então a validação não é afetada).
        return ".zip";
    }

    private static void ValidateArchive(string file)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file);
            _ = archive.Entries.Count;
        }
        catch
        {
            try { File.Delete(file); } catch { }
            throw new InvalidDataException("O arquivo baixado não é um pacote ZIP/.SMOD válido.");
        }
    }

    private sealed record ResolvedDownload(string Url, string? Name, string? Version);
}
