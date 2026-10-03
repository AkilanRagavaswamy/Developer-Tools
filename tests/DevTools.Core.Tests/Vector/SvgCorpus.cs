namespace DevTools.Core.Tests.Vector;

/// <summary>
/// The golden corpus the converter is held against (FR-V10).
/// </summary>
/// <remarks>
/// These are written the way real files are written, not the way a test author would write
/// them: Material and Feather icon paths verbatim, compressed arc flags, unitless and
/// unit-suffixed sizes, nested groups with transforms, gradients defined by reference, and the
/// unsupported features a designer's export drags along. Every one of them must emit XAML that
/// loads.
/// </remarks>
internal static class SvgCorpus
{
    private const string Ns = """xmlns="http://www.w3.org/2000/svg" """;

    public static IReadOnlyList<string> All { get; } =
    [
        // 1 — Material "home", a single filled path.
        $$"""<svg {{Ns}}width="24" height="24" viewBox="0 0 24 24"><path d="M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z"/></svg>""",

        // 2 — Material "delete", multiple subpaths in one d.
        $$"""<svg {{Ns}}viewBox="0 0 24 24"><path d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"/></svg>""",

        // 3 — Feather "check-circle": stroked, no fill, round caps.
        $$"""
        <svg {{Ns}}width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/>
        </svg>
        """,

        // 4 — Feather "activity": a polyline only.
        $$"""<svg {{Ns}}viewBox="0 0 24 24" fill="none" stroke="#333" stroke-width="2"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>""",

        // 5 — Compressed arc flags, the form drawing tools emit.
        $$"""<svg {{Ns}}viewBox="0 0 16 16"><path d="M8 0a8 8 0 100 16A8 8 0 008 0zm0 2a6 6 0 110 12A6 6 0 018 2z"/></svg>""",

        // 6 — Every path command in one file.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <path d="M10 10 H 90 V 90 H 10 L 10 10 Z M 20 20 C 25 25 35 25 40 20 S 55 15 60 20
                   Q 70 30 80 20 T 90 30 A 5 5 0 0 1 85 35 z" fill="#4F46E5"/>
        </svg>
        """,

        // 7 — Relative commands throughout.
        $$"""<svg {{Ns}}viewBox="0 0 50 50"><path d="m5 5 l10 0 l0 10 c5 5 10 5 15 0 q5 -5 10 0 z" fill="teal"/></svg>""",

        // 8 — The basic shapes.
        $$"""
        <svg {{Ns}}width="200" height="100" viewBox="0 0 200 100">
          <rect x="5" y="5" width="40" height="30" fill="red"/>
          <rect x="55" y="5" width="40" height="30" rx="6" ry="4" fill="orange"/>
          <circle cx="120" cy="20" r="15" fill="green"/>
          <ellipse cx="170" cy="20" rx="25" ry="12" fill="blue"/>
          <line x1="5" y1="50" x2="195" y2="50" stroke="black" stroke-width="2"/>
          <polyline points="5,60 50,90 95,60" fill="none" stroke="purple" stroke-width="3"/>
          <polygon points="120,60 150,90 105,90" fill="brown"/>
        </svg>
        """,

        // 9 — Nested groups with composed transforms.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <g transform="translate(10,10)">
            <g transform="scale(2) rotate(15)">
              <rect width="20" height="20" fill="#0EA5E9"/>
              <g transform="translate(5,5) skewX(10)"><circle cx="5" cy="5" r="3" fill="white"/></g>
            </g>
          </g>
        </svg>
        """,

        // 10 — matrix() directly.
        $$"""<svg {{Ns}}viewBox="0 0 100 100"><path d="M0 0 L20 0 L20 20 Z" transform="matrix(1.5,0.2,-0.2,1.5,20,20)" fill="#818CF8"/></svg>""",

        // 11 — Linear gradient, object bounding box.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <defs><linearGradient id="grad" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stop-color="#4F46E5"/><stop offset="100%" stop-color="#0EA5E9"/>
          </linearGradient></defs>
          <rect width="100" height="100" fill="url(#grad)"/>
        </svg>
        """,

        // 12 — Radial gradient with a focal point and stop-opacity.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <defs><radialGradient id="r" cx="0.3" cy="0.3" r="0.7" fx="0.25" fy="0.25">
            <stop offset="0" stop-color="#FFF" stop-opacity="0.9"/><stop offset="1" stop-color="#000"/>
          </radialGradient></defs>
          <circle cx="50" cy="50" r="50" fill="url(#r)"/>
        </svg>
        """,

        // 13 — userSpaceOnUse gradient inherited through xlink:href.
        """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
          <defs>
            <linearGradient id="base"><stop offset="0" stop-color="gold"/><stop offset="1" stop-color="crimson"/></linearGradient>
            <linearGradient id="use" xlink:href="#base" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0"/>
          </defs>
          <rect width="100" height="100" fill="url(#use)"/>
        </svg>
        """,

        // 14 — use, symbol and defs together.
        """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 120 40">
          <defs>
            <symbol id="star"><polygon points="10,0 12,7 20,7 13,11 16,19 10,14 4,19 7,11 0,7 8,7" fill="gold"/></symbol>
            <rect id="box" width="10" height="10" fill="#333"/>
          </defs>
          <use href="#star" x="0" y="10"/>
          <use xlink:href="#star" x="30" y="10"/>
          <use href="#box" x="60" y="15"/>
          <use href="#box" x="80" y="15" transform="rotate(45, 85, 20)"/>
        </svg>
        """,

        // 15 — clipPath.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <defs><clipPath id="c"><circle cx="50" cy="50" r="40"/></clipPath></defs>
          <rect width="100" height="100" fill="#4F46E5" clip-path="url(#c)"/>
        </svg>
        """,

        // 16 — Dashes, caps and joins.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 50">
          <path d="M5 25 L95 25" stroke="#111" stroke-width="4" stroke-dasharray="8 4" stroke-linecap="round"/>
          <path d="M5 40 L50 10 L95 40" fill="none" stroke="#c00" stroke-width="6"
                stroke-linejoin="miter" stroke-miterlimit="2" stroke-dasharray="3" stroke-dashoffset="1"/>
        </svg>
        """,

        // 17 — Inline style beating presentation attributes.
        $$"""<svg {{Ns}}viewBox="0 0 50 50"><rect width="50" height="50" fill="red" style="fill:#0EA5E9;stroke:#111;stroke-width:3"/></svg>""",

        // 18 — currentColor inherited from a group.
        $$"""<svg {{Ns}}viewBox="0 0 24 24" color="#E11D48"><path d="M12 2 L22 22 L2 22 Z" fill="currentColor"/></svg>""",

        // 19 — Opacity nested at three levels.
        $$"""
        <svg {{Ns}}viewBox="0 0 60 20">
          <g opacity="0.8"><g opacity="0.5">
            <rect width="20" height="20" fill="red" fill-opacity="0.5"/>
            <rect x="20" width="20" height="20" fill="green"/>
          </g></g>
        </svg>
        """,

        // 20 — even-odd fill rule, the classic donut.
        $$"""<svg {{Ns}}viewBox="0 0 100 100"><path fill-rule="evenodd" d="M50 10 A40 40 0 1 1 49 10 Z M50 30 A20 20 0 1 0 51 30 Z" fill="#111"/></svg>""",

        // 21 — preserveAspectRatio slice.
        $$"""<svg {{Ns}}width="200" height="50" viewBox="0 0 100 100" preserveAspectRatio="xMidYMid slice"><circle cx="50" cy="50" r="50" fill="#38BDF8"/></svg>""",

        // 22 — preserveAspectRatio none.
        $$"""<svg {{Ns}}width="200" height="50" viewBox="0 0 100 100" preserveAspectRatio="none"><rect width="100" height="100" fill="#84CC16"/></svg>""",

        // 23 — A viewBox with a non-zero origin.
        $$"""<svg {{Ns}}width="50" height="50" viewBox="-25 -25 50 50"><circle r="20" fill="#F59E0B"/></svg>""",

        // 24 — Physical units on the root.
        $$"""<svg {{Ns}}width="1in" height="0.5in" viewBox="0 0 96 48"><rect width="96" height="48" fill="#6366F1"/></svg>""",

        // 25 — No viewBox at all; user units are output units.
        $$"""<svg {{Ns}}width="40" height="40"><rect width="40" height="40" fill="#14B8A6"/></svg>""",

        // 26 — Unsupported features mixed in with real content.
        $$"""
        <svg {{Ns}}viewBox="0 0 100 100">
          <defs><filter id="blur"><feGaussianBlur stdDeviation="2"/></filter></defs>
          <title>An icon</title><desc>With metadata that must not be drawn</desc>
          <text x="10" y="20">not supported</text>
          <rect width="100" height="100" fill="#EC4899"/>
        </svg>
        """,

        // 27 — Every colour syntax in one document.
        $$"""
        <svg {{Ns}}viewBox="0 0 120 20">
          <rect width="20" height="20" fill="#f00"/>
          <rect x="20" width="20" height="20" fill="#00ff00"/>
          <rect x="40" width="20" height="20" fill="rgb(0,0,255)"/>
          <rect x="60" width="20" height="20" fill="rgba(255,0,255,0.5)"/>
          <rect x="80" width="20" height="20" fill="hsl(200, 80%, 50%)"/>
          <rect x="100" width="20" height="20" fill="rebeccapurple"/>
        </svg>
        """,

        // 28 — A designer export: nested svg, a stray class, decimals everywhere.
        $$"""
        <svg {{Ns}}version="1.1" id="Layer_1" x="0px" y="0px" viewBox="0 0 512 512" style="enable-background:new 0 0 512 512;">
          <g class="layer">
            <path class="st0" d="M256.0,12.8c-134.2,0-243.2,109.0-243.2,243.2s109.0,243.2,243.2,243.2
                 s243.2-109.0,243.2-243.2S390.2,12.8,256.0,12.8z M256.0,460.8c-113.0,0-204.8-91.8-204.8-204.8
                 S143.0,51.2,256.0,51.2S460.8,143.0,460.8,256.0S369.0,460.8,256.0,460.8z"/>
          </g>
        </svg>
        """,

        // 29 — A long multi-subpath logo-style path.
        $$"""
        <svg {{Ns}}viewBox="0 0 48 48">
          <path fill="#4285F4" d="M45.1 24.5c0-1.6-.1-3.2-.4-4.7H24v8.9h11.8c-.5 2.7-2 5-4.3 6.6v5.5h7c4.1-3.8 6.6-9.4 6.6-16.3z"/>
          <path fill="#34A853" d="M24 46c5.8 0 10.7-1.9 14.3-5.2l-7-5.5c-1.9 1.3-4.4 2.1-7.3 2.1-5.6 0-10.4-3.8-12.1-8.9H4.7v5.7C8.3 41.4 15.6 46 24 46z"/>
          <path fill="#FBBC05" d="M11.9 28.5c-.4-1.3-.7-2.7-.7-4.1s.3-2.8.7-4.1v-5.7H4.7C3.2 17.6 2.4 20.7 2.4 24s.8 6.4 2.3 9.2l7.2-4.7z"/>
          <path fill="#EA4335" d="M24 10.6c3.2 0 6 1.1 8.2 3.2l6.2-6.2C34.7 4.1 29.8 2 24 2 15.6 2 8.3 6.6 4.7 14.6l7.2 5.7c1.7-5.1 6.5-8.9 12.1-8.9z"/>
        </svg>
        """,

        // 30 — Degenerate and hostile input that must still emit something loadable.
        $$"""
        <svg {{Ns}}viewBox="0 0 10 10">
          <path d=""/>
          <rect width="0" height="0"/>
          <circle r="-5"/>
          <polygon points="1"/>
          <path d="M0 0 A 0 0 0 0 1 5 5"/>
          <rect width="5" height="5" fill="none"/>
          <rect width="5" height="5" fill="#123"/>
        </svg>
        """,
    ];
}
