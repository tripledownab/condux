using System.Text.Json;
using Condux.Core.Auth;
using Condux.Core.Http;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Reads a domain's TXT records over DNS-over-HTTPS to check an SSO domain claim (ADR-0043).
///
/// A thin <c>HttpClient</c> rather than a DNS library, for the reason the ADR gives: DoH returns JSON over
/// TLS, so the two hard parts of a resolver (the wire format and the transport) are already solved, and
/// what is left is reading one field. It also works unchanged in a container whose only resolver is
/// Docker's embedded one. Same shape as <see cref="SsoOidcClient"/> and the Stripe client, so a test stubs
/// its transport by named client and no interface exists only to be faked.
///
/// The resolvers differ in one way that matters here: Cloudflare returns each TXT value quoted and Google
/// returns it bare (measured against both). <see cref="DomainVerification.IsAnswered"/> owns that
/// difference, so this client hands over whatever the resolver said.
/// </summary>
internal sealed class DohTxtResolver(HttpClient http, IConfiguration cfg)
{
    private const int TxtRecordType = 16;
    private const string DefaultEndpoint = "https://cloudflare-dns.com/dns-query";

    /// <summary>
    /// Whether any of the claim's record names carries its token. Each name is queried in turn and the
    /// first hit wins, so an org that published the apex record is not also asked for the subdomain.
    ///
    /// NXDOMAIN is an answer, so a name that holds nothing is a missing record. But <c>RecordMissing</c>
    /// is only concluded when EVERY name answered: if one lookup failed, the record may well be on the
    /// name we could not read, and reporting it missing blames the org for a failure of ours.
    /// </summary>
    public async Task<DomainCheckOutcome> CheckAsync(
        string domain, string token, CancellationToken cancellationToken)
    {
        var everyNameAnswered = true;
        foreach (var name in DomainVerification.RecordNames(domain))
        {
            var values = await LookupTxtAsync(name, cancellationToken);
            if (values is null)
            {
                everyNameAnswered = false;
                continue;
            }

            if (DomainVerification.IsAnswered(values, token))
            {
                return DomainCheckOutcome.Verified;
            }
        }

        return everyNameAnswered ? DomainCheckOutcome.RecordMissing : DomainCheckOutcome.ResolverUnavailable;
    }

    /// <summary>The name's TXT values, or null when the resolver could not be reached or answered with a
    /// failure code. An empty list means it answered and the name holds no TXT records.</summary>
    private async Task<IReadOnlyList<string>?> LookupTxtAsync(string name, CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?name={Uri.EscapeDataString(name)}&type=TXT";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Cloudflare serves the JSON form only for this Accept header; Google ignores it. Sending it
        // always keeps one request shape for both.
        request.Headers.Add("accept", "application/dns-json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, which surfaces as TaskCanceledException with an inner
            // TimeoutException rather than as HttpRequestException (measured on net10.0). Without this
            // case the timeout configured for this client would throw out of the verify request and
            // answer 500, for exactly the hanging resolver the timeout exists to bound. A cancellation
            // that IS the caller's token still propagates: the browser went away and there is nobody to
            // answer.
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // Parsed from the string rather than with ReadFromJsonAsync, which validates the media type:
            // Cloudflare answers application/dns-json, which has neither the application/json type nor a
            // +json suffix, so the typed read would refuse the very resolver this defaults to. Same shape
            // as OidcExchange and StripeClient.
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(payload);
                return ReadAnswers(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The TXT values in a DoH answer, or null when the resolver declined to answer.</summary>
    private static IReadOnlyList<string>? ReadAnswers(JsonElement root)
    {
        // Status 0 is NOERROR and 3 is NXDOMAIN. Both are answers: the name either holds TXT records or it
        // does not. Any other code is the resolver saying it could not tell us.
        if (!root.TryGetProperty("Status", out var status)
            || status.ValueKind != JsonValueKind.Number
            || (status.GetInt32() != 0 && status.GetInt32() != 3))
        {
            return null;
        }

        if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Filtered on the record type because a name that is a CNAME answers with the CNAME record
        // alongside the TXT ones, and its target is not a value anybody published for us.
        return answers.EnumerateArray()
            .Where(a => a.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.Number && type.GetInt32() == TxtRecordType)
            .Select(a => a.TryGetProperty("data", out var data) ? data.GetString() : null)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }

    // Overridable so an operator who will not send their customers' domain names to a third party can
    // point this at their own resolver. The default is the public one the ADR settled on.
    //
    // Absent is fine and takes the default. Present and malformed is not: at the sink the value IS the
    // endpoint. Measured on net10.0, a scheme-less value builds a relative URI that HttpClient refuses
    // with an InvalidOperationException naming neither this variable nor SSO, so every Verify click would
    // answer 500 and the log would point at nothing. The shape check is the shared HttpUrls one rather
    // than a second URL rule of this file's own.
    //
    // It refuses rather than falling back to the default, because an operator sets this precisely so
    // their customers' domain names do not reach a public resolver, and quietly using one anyway would
    // undo the only reason the setting exists.
    //
    // Deliberately not covered by a test: both the named and the unnamed failure surface as the same 500
    // with an empty body, so the difference is visible only in the log, and a test over the HTTP surface
    // would pass with this check deleted.
    private string Endpoint
    {
        get
        {
            if (cfg["CONDUX_DOH_ENDPOINT"] is not { Length: > 0 } custom)
            {
                return DefaultEndpoint;
            }
            return HttpUrls.IsAbsoluteHttp(custom)
                ? custom
                : throw new InvalidOperationException(
                    "CONDUX_DOH_ENDPOINT must be an absolute http(s) URL, for example "
                    + $"{DefaultEndpoint}. Unset it to use the default resolver.");
        }
    }
}
