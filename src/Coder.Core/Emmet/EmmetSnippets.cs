using System.Collections.Frozen;

namespace Coder.Core.Emmet;

/// <summary>
/// Emmet's standard HTML snippets (the table VS Code ships), each an abbreviation itself:
/// <c>inp</c> is <c>input[…]</c>, and <c>input</c> is a snippet too, so attributes stack from the
/// innermost definition out. <c>a|b</c> keys are aliases. Fields use snippet syntax
/// (<c>${1:text}</c>); <c>/</c> marks an element without a closing tag.
/// </summary>
internal static class EmmetSnippets
{
    public static readonly FrozenDictionary<string, string> Html = Build(new Dictionary<string, string>
    {
        ["a"] = "a[href]",
        ["a:blank"] = "a[href='http://${0}' target='_blank' rel='noopener noreferrer']",
        ["a:link"] = "a[href='http://${0}']",
        ["a:mail"] = "a[href='mailto:${0}']",
        ["a:tel"] = "a[href='tel:+${0}']",
        ["abbr"] = "abbr[title]",
        ["acr|acronym"] = "acronym[title]",
        ["base"] = "base[href]/",
        ["basefont"] = "basefont/",
        ["br"] = "br/",
        ["frame"] = "frame/",
        ["hr"] = "hr/",
        ["bdo"] = "bdo[dir]",
        ["bdo:r"] = "bdo[dir=rtl]",
        ["bdo:l"] = "bdo[dir=ltr]",
        ["col"] = "col/",
        ["link"] = "link[rel=stylesheet href]/",
        ["link:css"] = "link[href='${1:style}.css']",
        ["link:print"] = "link[href='${1:print}.css' media=print]",
        ["link:favicon"] = "link[rel='shortcut icon' type=image/x-icon href='${1:favicon.ico}']",
        ["link:mf|link:manifest"] = "link[rel='manifest' href='${1:manifest.json}']",
        ["link:touch"] = "link[rel=apple-touch-icon href='${1:favicon.png}']",
        ["link:rss"] = "link[rel=alternate type=application/rss+xml title=RSS href='${1:rss.xml}']",
        ["link:atom"] = "link[rel=alternate type=application/atom+xml title=Atom href='${1:atom.xml}']",
        ["link:im|link:import"] = "link[rel=import href='${1:component}.html']",
        ["meta"] = "meta/",
        ["meta:utf"] = "meta[http-equiv=Content-Type content='text/html;charset=UTF-8']",
        ["meta:vp"] = "meta[name=viewport content='width=${1:device-width}, initial-scale=${2:1.0}']",
        ["meta:compat"] = "meta[http-equiv=X-UA-Compatible content='${1:IE=7}']",
        ["meta:edge"] = "meta:compat[content='${1:ie=edge}']",
        ["meta:redirect"] = "meta[http-equiv=refresh content='0; url=${1:http://example.com}']",
        ["meta:refresh"] = "meta[http-equiv=refresh content='${1:5}']",
        ["meta:kw"] = "meta[name=keywords content]",
        ["meta:desc"] = "meta[name=description content]",
        ["style"] = "style",
        ["script"] = "script",
        ["script:src"] = "script[src]",
        ["script:module"] = "script[type=module src]",
        ["img"] = "img[src alt]/",
        ["img:s|img:srcset"] = "img[srcset src alt]",
        ["img:z|img:sizes"] = "img[sizes srcset src alt]",
        ["picture"] = "picture",
        ["src|source"] = "source/",
        ["src:sc|source:src"] = "source[src type]",
        ["src:s|source:srcset"] = "source[srcset]",
        ["src:t|source:type"] = "source[srcset type='${1:image/}']",
        ["src:z|source:sizes"] = "source[sizes srcset]",
        ["src:m|source:media"] = "source[media='(${1:min-width: })' srcset]",
        ["src:mt|source:media:type"] = "source:media[type='${2:image/}']",
        ["src:mz|source:media:sizes"] = "source:media[sizes srcset]",
        ["src:zt|source:sizes:type"] = "source[sizes srcset type='${1:image/}']",
        ["iframe"] = "iframe[src frameborder=0]",
        ["embed"] = "embed[src type]/",
        ["object"] = "object[data type]",
        ["param"] = "param[name value]/",
        ["map"] = "map[name]",
        ["area"] = "area[shape coords href alt]/",
        ["area:d"] = "area[shape=default]",
        ["area:c"] = "area[shape=circle]",
        ["area:r"] = "area[shape=rect]",
        ["area:p"] = "area[shape=poly]",
        ["form"] = "form[action]",
        ["form:get"] = "form[method=get]",
        ["form:post"] = "form[method=post]",
        ["label"] = "label[for]",
        ["input"] = "input[type=${1:text}]/",
        ["inp"] = "input[type=text name=${1} id=${1}]",
        ["input:h|input:hidden"] = "input[type=hidden name]",
        ["input:t|input:text"] = "inp[type=text]",
        ["input:search"] = "inp[type=search]",
        ["input:email"] = "inp[type=email]",
        ["input:url"] = "inp[type=url]",
        ["input:p|input:password"] = "inp[type=password]",
        ["input:datetime"] = "inp[type=datetime]",
        ["input:date"] = "inp[type=date]",
        ["input:datetime-local"] = "inp[type=datetime-local]",
        ["input:month"] = "inp[type=month]",
        ["input:week"] = "inp[type=week]",
        ["input:time"] = "inp[type=time]",
        ["input:tel"] = "inp[type=tel]",
        ["input:number"] = "inp[type=number]",
        ["input:color"] = "inp[type=color]",
        ["input:c|input:checkbox"] = "inp[type=checkbox]",
        ["input:r|input:radio"] = "inp[type=radio]",
        ["input:range"] = "inp[type=range]",
        ["input:f|input:file"] = "inp[type=file]",
        ["input:s|input:submit"] = "input[type=submit value]",
        ["input:i|input:image"] = "input[type=image src alt]",
        ["input:b|input:btn|input:button"] = "input[type=button value]",
        ["input:reset"] = "input[type=reset value]",
        ["isindex"] = "isindex/",
        ["select"] = "select[name=${1} id=${1}]",
        ["select:d|select:disabled"] = "select[disabled.]",
        ["opt|option"] = "option[value]",
        ["textarea"] = "textarea[name=${1} id=${1} cols=${2:30} rows=${3:10}]",
        ["marquee"] = "marquee[behavior direction]",
        ["menu:c|menu:context"] = "menu[type=context]",
        ["menu:t|menu:toolbar"] = "menu[type=toolbar]",
        ["video"] = "video[src]",
        ["audio"] = "audio[src]",
        ["html:xml"] = "html[xmlns=http://www.w3.org/1999/xhtml]",
        ["keygen"] = "keygen/",
        ["command"] = "command/",
        ["btn:s|button:s|button:submit"] = "button[type=submit]",
        ["btn:r|button:r|button:reset"] = "button[type=reset]",
        ["btn:b|button:b|button:button"] = "button[type=button]",
        ["btn:d|button:d|button:disabled"] = "button[disabled.]",
        ["fst:d|fset:d|fieldset:d|fieldset:disabled"] = "fieldset[disabled.]",
        ["bq"] = "blockquote",
        ["fig"] = "figure",
        ["figc"] = "figcaption",
        ["pic"] = "picture",
        ["ifr"] = "iframe",
        ["emb"] = "embed",
        ["obj"] = "object",
        ["cap"] = "caption",
        ["colg"] = "colgroup",
        ["fst|fset"] = "fieldset",
        ["btn"] = "button",
        ["optg"] = "optgroup",
        ["tarea"] = "textarea",
        ["leg"] = "legend",
        ["sect"] = "section",
        ["art"] = "article",
        ["hdr"] = "header",
        ["ftr"] = "footer",
        ["adr"] = "address",
        ["dlg"] = "dialog",
        ["str"] = "strong",
        ["prog"] = "progress",
        ["mn"] = "main",
        ["tem"] = "template",
        ["datag"] = "datagrid",
        ["datal"] = "datalist",
        ["kg"] = "keygen",
        ["out"] = "output",
        ["det"] = "details",
        ["sum"] = "summary",
        ["cmd"] = "command",
        ["data"] = "data[value]",
        ["meter"] = "meter[value]",
        ["time"] = "time[datetime]",
        ["ri:d|ri:dpr"] = "img:s",
        ["ri:v|ri:viewport"] = "img:z",
        ["ri:a|ri:art"] = "pic>src:m+img",
        ["ri:t|ri:type"] = "pic>src:t+img",
        ["!!!"] = "{<!DOCTYPE html>}",
        ["doc"] = "html[lang=en]>(head>meta[charset=UTF-8]+meta[name=viewport content='width=device-width, initial-scale=1.0']+title{${1:Document}})+body",
        ["!|html:5"] = "!!!+doc",
        ["c"] = "{<!-- ${0} -->}",
        ["cc:ie"] = "{<!--[if IE]>${0}<![endif]-->}",
        ["cc:noie"] = "{<!--[if !IE]><!-->${0}<!--<![endif]-->}",
        ["ol+"] = "ol>li",
        ["ul+"] = "ul>li",
        ["dl+"] = "dl>dt+dd",
        ["map+"] = "map>area",
        ["table+"] = "table>tr>td",
        ["tr+"] = "tr>td",
        ["select+"] = "select>option",
        ["optgroup+|optg+"] = "optgroup>option",
        ["colgroup+|colg+"] = "colgroup>col",
    });

    /// <summary>HTML element names: an abbreviation that is just one of these is worth offering.</summary>
    public static readonly FrozenSet<string> Tags = (
        "a abbr address area article aside audio b base bdi bdo blockquote body br button canvas caption cite code col colgroup " +
        "data datalist dd del details dfn dialog div dl dt em embed fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 head " +
        "header hgroup hr html i iframe img input ins kbd label legend li link main map mark menu meta meter nav noscript object ol " +
        "optgroup option output p param picture pre progress q rp rt ruby s samp script search section select slot small source span " +
        "strong style sub summary sup svg table tbody td template textarea tfoot th thead time title tr track u ul var video wbr")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Elements kept on the line of their neighbours (Emmet's "inline level" list).</summary>
    public static readonly FrozenSet<string> Inline = (
        "a abbr acronym applet b basefont bdo big br button cite code del dfn em font i iframe img input ins kbd label map object q s " +
        "samp select small span strike strong sub sup textarea tt u var")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Elements without content or closing tag.</summary>
    public static readonly FrozenSet<string> Void = (
        "area base br col command embed hr img input keygen link meta param source track wbr")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    private static FrozenDictionary<string, string> Build(Dictionary<string, string> source)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keys, value) in source)
        {
            foreach (var key in keys.Split('|'))
            {
                table[key] = value;
            }
        }

        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
