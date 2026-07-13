using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Klip.Engine;

public sealed record DomLink(string Href, string Text);
public sealed record DomImage(string Src, string Alt);

/// <summary>Mapa estruturado de uma página — o shape canónico partilhado por C# (Parse) e JS (ExtractJs).</summary>
public sealed record DomExtractResult(
    string Title,
    IReadOnlyList<string> Headings,
    IReadOnlyList<DomLink> Links,
    IReadOnlyList<DomImage> Images,
    IReadOnlyList<string> Videos,
    IReadOnlyList<string> Audios,
    string Text);

/// <summary>
/// Extração PURA de HTML → estrutura (title/headings/links/images/videos/audios/text), com
/// absolutização de URLs relativos. Sem WebView2, sem rede → testável offline contra HTML fixo.
/// O <see cref="ExtractJs"/> injetado no browser devolve EXATAMENTE o mesmo JSON.
/// </summary>
public static class DomExtract
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;
    private static readonly Regex RxTitle = new(@"<title[^>]*>(.*?)</title>", O);
    private static readonly Regex RxHead = new(@"<(h[1-3])[^>]*>(.*?)</\1>", O);
    private static readonly Regex RxAnchor = new(@"<a\b[^>]*?href\s*=\s*(""|')(?<href>.*?)\1[^>]*>(?<text>.*?)</a>", O);
    private static readonly Regex RxImg = new(@"<img\b[^>]*?>", O);
    private static readonly Regex RxAttrSrc = new(@"\bsrc\s*=\s*(""|')(?<v>.*?)\1", O);
    private static readonly Regex RxAttrDataSrc = new(@"\bdata-src\s*=\s*(""|')(?<v>.*?)\1", O);
    private static readonly Regex RxAttrAlt = new(@"\balt\s*=\s*(""|')(?<v>.*?)\1", O);
    private static readonly Regex RxVideoBlk = new(@"<video[^>]*\bsrc\s*=\s*(""|')(?<v>.*?)\1|<video\b[\s\S]*?</video>", O);
    private static readonly Regex RxAudioBlk = new(@"<audio[^>]*\bsrc\s*=\s*(""|')(?<v>.*?)\1|<audio\b[\s\S]*?</audio>", O);
    private static readonly Regex RxSource = new(@"<source[^>]*\bsrc\s*=\s*(""|')(?<v>.*?)\1", O);
    private static readonly Regex RxTag = new(@"<[^>]+>", O);
    private static readonly Regex RxScriptStyle = new(@"<(script|style)[^>]*>[\s\S]*?</\1>", O);
    private static readonly Regex RxWs = new(@"\s+", RegexOptions.Compiled);

    private static string Clean(string s, int cap) => Trunc(WebUtility.HtmlDecode(RxWs.Replace(RxTag.Replace(s, " "), " ").Trim()), cap);
    private static string Trunc(string s, int cap) => s.Length <= cap ? s : s[..cap];

    /// <summary>Resolve um URL (relativo/protocol-relative) contra baseUrl → absoluto. data:/http(s) passam.</summary>
    public static string Absolutize(string? baseUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith("//"))
        {
            var scheme = baseUrl != null && baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
            return scheme + ":" + url;
        }
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
            && Uri.TryCreate(b, url, out var abs)) return abs.ToString();
        return url;
    }

    public static DomExtractResult Parse(string html, string? baseUrl = null)
    {
        html ??= "";
        string title = RxTitle.Match(html) is { Success: true } tm ? WebUtility.HtmlDecode(RxWs.Replace(tm.Groups[1].Value, " ").Trim()) : "";

        var headings = new List<string>();
        foreach (Match m in RxHead.Matches(html))
        {
            if (headings.Count >= 40) break;
            var h = Clean(m.Groups[2].Value, 120);
            if (h.Length > 0) headings.Add(h);
        }

        var links = new List<DomLink>(); var seenL = new HashSet<string>();
        foreach (Match m in RxAnchor.Matches(html))
        {
            if (links.Count >= 200) break;
            var href = Absolutize(baseUrl, WebUtility.HtmlDecode(m.Groups["href"].Value.Trim()));
            if (href.Length == 0 || href == "#" || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            var linkText = Clean(m.Groups["text"].Value, 80);
            if (seenL.Add(linkText + "|" + href)) links.Add(new DomLink(href, linkText));
        }

        var images = new List<DomImage>(); var seenI = new HashSet<string>();
        foreach (Match m in RxImg.Matches(html))
        {
            if (images.Count >= 200) break;
            var tag = m.Value;
            var srcM = RxAttrSrc.Match(tag);
            var raw = srcM.Success ? srcM.Groups["v"].Value : RxAttrDataSrc.Match(tag) is { Success: true } dm ? dm.Groups["v"].Value : "";
            var src = Absolutize(baseUrl, WebUtility.HtmlDecode(raw.Trim()));
            if (src.Length == 0 || !seenI.Add(src)) continue;
            var altM = RxAttrAlt.Match(tag);
            images.Add(new DomImage(src, altM.Success ? Trunc(WebUtility.HtmlDecode(altM.Groups["v"].Value), 120) : ""));
        }

        var videos = MediaSrcs(html, RxVideoBlk, baseUrl);
        var audios = MediaSrcs(html, RxAudioBlk, baseUrl);

        string text = Trunc(WebUtility.HtmlDecode(RxWs.Replace(RxTag.Replace(RxScriptStyle.Replace(html, " "), " "), " ").Trim()), 4000);

        return new DomExtractResult(title, headings, links, images, videos, audios, text);
    }

    // src directos no tag OU dentro de <source> em cada bloco de media, absolutizados + dedupe.
    private static List<string> MediaSrcs(string html, Regex blk, string? baseUrl)
    {
        var outp = new List<string>(); var seen = new HashSet<string>();
        void Add(string raw)
        {
            var s = Absolutize(baseUrl, WebUtility.HtmlDecode(raw.Trim()));
            if (s.Length > 0 && seen.Add(s) && outp.Count < 100) outp.Add(s);
        }
        foreach (Match m in blk.Matches(html))
        {
            if (m.Groups["v"].Success && m.Groups["v"].Value.Length > 0) Add(m.Groups["v"].Value);   // src directo no tag
            else foreach (Match s in RxSource.Matches(m.Value)) Add(s.Groups["v"].Value);             // <source> dentro do bloco
        }
        return outp;
    }

    /// <summary>JS injetado no browser (via Browser.InvokeScript) → devolve o MESMO shape JSON que Parse.</summary>
    public const string ExtractJs = @"(function(){
  function abs(u){ try{ return new URL(u, document.baseURI).href; } catch(e){ return u||''; } }
  function cl(s){ return (s||'').replace(/\s+/g,' ').trim(); }
  var links=[], sl={};
  document.querySelectorAll('a[href]').forEach(function(a){ if(links.length>=200) return;
    var h=abs(a.getAttribute('href')||''); var t=cl(a.innerText||a.textContent||'').slice(0,80);
    if(!h||h==='#'||h.indexOf('javascript:')===0) return; var k=t+'|'+h; if(sl[k]) return; sl[k]=1; links.push({href:h,text:t}); });
  var images=[], si={};
  document.querySelectorAll('img').forEach(function(i){ if(images.length>=200) return;
    var s=abs(i.getAttribute('src')||i.getAttribute('data-src')||''); if(!s||si[s]) return; si[s]=1;
    images.push({src:s,alt:(i.getAttribute('alt')||'').slice(0,120)}); });
  var videos=[], sv={};
  document.querySelectorAll('video[src],video source[src]').forEach(function(v){ var s=abs(v.getAttribute('src')||''); if(!s||sv[s]) return; sv[s]=1; if(videos.length<100) videos.push(s); });
  var audios=[], sa={};
  document.querySelectorAll('audio[src],audio source[src]').forEach(function(v){ var s=abs(v.getAttribute('src')||''); if(!s||sa[s]) return; sa[s]=1; if(audios.length<100) audios.push(s); });
  var heads=[];
  document.querySelectorAll('h1,h2,h3').forEach(function(h){ if(heads.length<40){ var t=cl(h.innerText||'').slice(0,120); if(t) heads.push(t); } });
  var text=cl(document.body?document.body.innerText:'').slice(0,4000);
  return JSON.stringify({title:cl(document.title||''), headings:heads, links:links, images:images, videos:videos, audios:audios, text:text});
})()";
}
