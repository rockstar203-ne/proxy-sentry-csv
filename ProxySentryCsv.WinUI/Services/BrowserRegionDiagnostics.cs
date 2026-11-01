using System.Text.Json;

namespace ProxySentryCsv.Services;

internal static class BrowserRegionDiagnostics
{
    public const string ReadExpression = "JSON.stringify({timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,locale:Intl.DateTimeFormat().resolvedOptions().locale,language:navigator.language,languages:Array.from(navigator.languages),offset:new Date().getTimezoneOffset(),userAgent:navigator.userAgent})";

    public static string Describe(JsonElement response, BrowserForgeFingerprint expected)
    {
        if (response.TryGetProperty("exceptionDetails", out _) || !response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
            return "Regional check: unavailable (browser did not return values).";
        using var data = JsonDocument.Parse(value.GetString()!);
        var root = data.RootElement;
        var zone = root.GetProperty("timezone").GetString() ?? "";
        var language = root.GetProperty("language").GetString() ?? "";
        var locale = root.GetProperty("locale").GetString() ?? "";
        var comparable = expected.TimeZone.Length > 0 && expected.Locale.Length > 0;
        var matches = SameZone(zone, expected.TimeZone) && string.Equals(language, expected.Locale, StringComparison.OrdinalIgnoreCase)
            && string.Equals(locale, expected.Locale, StringComparison.OrdinalIgnoreCase);
        var status = !comparable ? "NO VERIFIED EXPECTATION" : matches ? "MATCHES CONFIGURATION" : "MISMATCH";
        return $"{status}\nTimezone: {zone}\nLanguage: {language}\nIntl locale: {locale}\nGeo source: {expected.GeoSource}";
    }

    internal static bool SameZone(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
        static string Canonical(string zone) => zone.ToLowerInvariant() switch
        {
            "europe/kiev" => "europe/kyiv",
            "asia/calcutta" => "asia/kolkata",
            "us/pacific" => "america/los_angeles",
            "etc/utc" or "etc/gmt" or "gmt" => "utc",
            _ => zone.ToLowerInvariant()
        };
        return Canonical(actual) == Canonical(expected);
    }

    public static string BuildLocalPage(BrowserForgeFingerprint config)
    {
        // Only expected locale/timezone are embedded. No proxy address, credentials or email.
        var expected = JsonSerializer.Serialize(new { timezone = config.TimeZone, language = config.Locale, source = config.GeoSource, warning = config.GeoWarning });
        return """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; worker-src blob:; connect-src 'none'">
<title>Proxy Sentry - Regional check</title>
<style>body{font:15px system-ui;margin:24px;background:#202020;color:#eee;letter-spacing:0}main{max-width:850px;margin:auto}h1{font-size:22px}h2{font-size:17px}pre{white-space:pre-wrap;overflow-wrap:anywhere;border-top:1px solid #555;padding:12px 0}button{font:inherit;padding:8px 16px;background:#eee;color:#111;border:0;border-radius:4px}#status{font-weight:600;color:#edbd62}</style>
<main><h1>Regional check</h1><p>Local report. No network requests.</p><p id="status">Checking...</p><button onclick="location.reload()">Check again</button>
<h2>Expected configuration</h2><pre id="expected"></pre><h2>This tab</h2><pre id="page"></pre><h2>Dedicated worker</h2><pre id="worker">Checking...</pre></main>
<script>
const expected =
""" + expected + ";\n" + """
document.querySelector('#expected').textContent=JSON.stringify(expected,null,2);
const read=()=>({timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,locale:Intl.DateTimeFormat().resolvedOptions().locale,language:navigator.language,languages:Array.from(navigator.languages),offset:new Date().getTimezoneOffset(),userAgent:navigator.userAgent});
const page=read(); document.querySelector('#page').textContent=JSON.stringify(page,null,2);
const canonical=z=>{try{return new Intl.DateTimeFormat('en',{timeZone:z}).resolvedOptions().timeZone}catch{return ''}};
const matches=v=>!!expected.timezone&&!!expected.language&&canonical(v.timezone)===canonical(expected.timezone)&&v.language.toLowerCase()===expected.language.toLowerCase()&&v.locale.toLowerCase()===expected.language.toLowerCase();
let worker, url, timer;
const finish=(value,error)=>{
 clearTimeout(timer); if(worker)worker.terminate(); if(url)URL.revokeObjectURL(url);
 document.querySelector('#worker').textContent=error||JSON.stringify(value,null,2);
 document.querySelector('#status').textContent=error?'Worker not verified':!expected.timezone||!expected.language?'No verified regional expectation':matches(page)&&matches(value)?'Tab and worker match configuration':'Regional mismatch';
};
try{url=URL.createObjectURL(new Blob(['postMessage(('+read.toString()+')())'],{type:'text/javascript'}));worker=new Worker(url);worker.onmessage=e=>finish(e.data);worker.onerror=()=>finish(null,'Worker check unavailable');timer=setTimeout(()=>finish(null,'Worker check timed out'),3000)}catch{finish(null,'Worker check unavailable')}
</script></html>
""";
    }
}
