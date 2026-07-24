using System.Globalization;
using System.Text;
using GnOuGo.Assets.Bears;

namespace GnOuGo.Assets.Animation;

public static class GnouGnouAnimationSvgRenderer
{
    public static GnouGnouAnimationSvgDocument Render(
        GnouGnouAnimationPlan plan,
        GnouGnouAnimationRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new GnouGnouAnimationRenderOptions();
        if (options.Width is < 640 or > 4096 || options.Height is < 360 or > 2160)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum SVG dimensions must be between 640x360 and 4096x2160.");

        var canvasWidth = Math.Max(options.Width, (int)Math.Ceiling(plan.Bounds.Width));
        var canvasHeight = Math.Max(options.Height, (int)Math.Ceiling(plan.Bounds.Height));
        var title = Escape(options.Title ?? $"GnOuGo team simulation · {plan.Entrypoint}");
        var description = Escape(options.Description ??
            $"Synthetic workflow preview in a {plan.Scene.ToString().ToLowerInvariant()} scene with {plan.Actors.Count} GnOuGo actors.");
        var builder = new StringBuilder(80_000);
        builder.Append("<svg width=\"").Append(canvasWidth).Append("\" height=\"").Append(canvasHeight)
            .Append("\" viewBox=\"0 0 ").Append(canvasWidth).Append(' ').Append(canvasHeight)
            .Append("\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"animation-title animation-desc\" data-scene=\"")
            .Append(plan.Scene.ToString().ToLowerInvariant()).AppendLine("\">");
        builder.Append("  <title id=\"animation-title\">").Append(title).AppendLine("</title>");
        builder.Append("  <desc id=\"animation-desc\">").Append(description).AppendLine("</desc>");
        AppendDefs(builder);
        AppendScene(builder, plan.Scene, plan.Seed, canvasWidth, canvasHeight);
        AppendLanes(builder, plan.Lanes);
        AppendHeader(builder, plan, canvasWidth);
        AppendEdges(builder, plan.Edges, plan.Nodes);
        AppendFlowNodes(builder, plan.Nodes);
        AppendStations(builder, plan.Stations, plan.Nodes);
        builder.AppendLine("  <g id=\"motion-trails\" aria-hidden=\"true\" pointer-events=\"none\"/>");
        AppendTasks(builder, plan);
        AppendActors(builder, plan);
        builder.AppendLine("</svg>");

        return new GnouGnouAnimationSvgDocument(builder.ToString(), canvasWidth, canvasHeight, plan.Scene, plan.DurationMs);
    }

    private static void AppendDefs(StringBuilder builder)
    {
        builder.AppendLine("""
  <defs>
    <linearGradient id="scene-sky" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#bce8ff"/><stop offset="1" stop-color="#f8fdff"/>
    </linearGradient>
    <linearGradient id="scene-floor" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#d9ebf5"/><stop offset="1" stop-color="#b7cedd"/>
    </linearGradient>
    <filter id="scene-shadow" x="-30%" y="-30%" width="160%" height="160%">
      <feDropShadow dx="0" dy="8" stdDeviation="8" flood-color="#17324d" flood-opacity=".18"/>
    </filter>
    <filter id="matrix-glow" x="-40%" y="-40%" width="180%" height="180%">
      <feGaussianBlur stdDeviation="4" result="blur"/><feFlood flood-color="#38f8df" flood-opacity=".9"/>
      <feComposite in2="blur" operator="in"/><feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <filter id="route-rough" x="-12%" y="-25%" width="124%" height="150%">
      <feTurbulence type="fractalNoise" baseFrequency=".018 .09" numOctaves="2" seed="17" result="noise"/>
      <feDisplacementMap in="SourceGraphic" in2="noise" scale="7" xChannelSelector="R" yChannelSelector="B"/>
    </filter>
    <linearGradient id="desk-top" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#e7ba7c"/><stop offset="1" stop-color="#b9773f"/>
    </linearGradient>
    <linearGradient id="laptop-screen" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#183f5c"/><stop offset="1" stop-color="#071c2c"/>
    </linearGradient>
  </defs>
  <style>
    .gnougo-actor { transition: opacity .25s ease, filter .25s ease; }
    .gnougo-actor[data-visible="false"], .task-object[data-visible="false"] { opacity: 0; pointer-events: none; }
    .gnougo-actor.is-clone { filter: url(#matrix-glow); opacity: .82; }
    .flow-lane { fill: rgba(255,255,255,.64); stroke: rgba(43,85,122,.32); stroke-width: 3; }
    .flow-edge { transition: opacity .2s ease; }
    .flow-edge[data-selected="false"] { opacity: .28; }
    .route-shadow, .route-bed, .route-surface, .route-ruts { fill: none; stroke-linecap: round; stroke-linejoin: round; }
    .route-shadow { stroke: rgba(17,24,28,.14); stroke-width: 50; transform: translate(0 7px); }
    .route-bed { stroke: rgba(22,29,33,.34); stroke-width: 46; }
    .route-surface { stroke: rgba(55,65,71,.59); stroke-width: 36; transition: stroke .2s ease, filter .2s ease; }
    .route-ruts { stroke: rgba(225,232,234,.34); stroke-width: 2.5; stroke-dasharray: 6 19; opacity: .8; }
    .route-stone { fill: rgba(91,101,106,.64); stroke: rgba(35,43,47,.72); stroke-width: 2; }
    .flow-edge.is-active .route-surface { stroke: rgba(78,145,187,.76); filter: url(#matrix-glow); }
    .flow-edge.is-active .route-ruts { stroke: rgba(225,250,255,.78); }
    .flow-edge.is-success .route-surface { stroke: rgba(74,144,105,.67); }
    .flow-edge.is-failed .route-surface { stroke: rgba(187,73,82,.73); }
    .flow-node .node-shape { fill: #fff; stroke: #315f82; stroke-width: 4; }
    .flow-node[data-selected="false"] { opacity: .4; }
    .sign-face { fill: #fff8d6; stroke: #674c33; stroke-width: 4; }
    .sign-top { fill: #ffe9a8; stroke: #674c33; stroke-width: 4; }
    .sign-side { fill: #c58a45; stroke: #674c33; stroke-width: 4; }
    .sign-post { fill: #8d5b32; stroke: #59381f; stroke-width: 3; }
    .sign-glyph { font-size: 37px; font-weight: 950; text-anchor: middle; fill: #315f82; }
    .flow-node.is-active .sign-face { fill: #fff2a8; stroke: #f0a52d; filter: url(#matrix-glow); }
    .flow-node.is-success .sign-face { fill: #dff8e9; stroke: #2c9c5b; }
    .flow-node.is-failed .sign-face { fill: #ffe1e4; stroke: #d84453; }
    .task-object { transition: opacity .2s ease, filter .2s ease; }
    .task-object .task-aura { opacity: .2; }
    .task-object.is-working .task-aura { opacity: .9; }
    .task-object.is-complete .task-body { fill: #42c97b; stroke: #17693c; }
    .task-object.is-failed .task-body { fill: #ef5b67; stroke: #9d2634; }
    .task-label { font-family: Inter, ui-sans-serif, system-ui, sans-serif; font-size: 12px; font-weight: 800; text-anchor: middle; fill: #18344d; }
    .task-glyph { font-family: Inter, ui-sans-serif, system-ui, sans-serif; font-size: 25px; font-weight: 900; text-anchor: middle; dominant-baseline: middle; fill: #fff; }
    .station-card { fill: rgba(255,255,255,.96); stroke: #2b557a; stroke-width: 3; }
    .desk-top { fill: url(#desk-top); stroke: #603c25; stroke-width: 4; }
    .desk-front { fill: #945b34; stroke: #603c25; stroke-width: 4; }
    .desk-side { fill: #704228; stroke: #4c2d1c; stroke-width: 4; }
    .desk-leg { fill: #6a4029; stroke: #452817; stroke-width: 3; }
    .desk-shadow { fill: rgba(45,37,31,.2); }
    .laptop-shell { fill: #8da2ae; stroke: #324a59; stroke-width: 4; }
    .laptop-side { fill: #627985; stroke: #324a59; stroke-width: 3; }
    .laptop-base-front { fill: #667d88; stroke: #324a59; stroke-width: 3; }
    .laptop-hinge { fill: none; stroke: #263f4d; stroke-width: 5; stroke-linecap: round; }
    .laptop-trackpad { fill: #aebdc4; stroke: #536a75; stroke-width: 2; }
    .laptop-screen { fill: url(#laptop-screen); stroke: #071e2d; stroke-width: 3; }
    #workstations > g.is-active .desk-top { fill: #f1c76f; stroke: #f0a52d; filter: url(#matrix-glow); }
    #workstations > g.is-active .laptop-screen { stroke: #72e8d0; }
    #workstations > g.is-active .station-label { fill: #8c4c12; }
    #workstations > g.is-success .desk-top { fill: #a8dbad; stroke: #35a867; }
    #workstations > g.is-failed .desk-top { fill: #ed9a9f; stroke: #d94755; }
    .desk-screen { fill: #163b55; stroke: #071e2d; stroke-width: 4; }
    .desk-screen-line { stroke: #72e8d0; stroke-width: 4; stroke-linecap: round; opacity: .72; }
    .desk-key { fill: #d9e7ef; stroke: #47677d; stroke-width: 1.2; }
    #workstations > g.is-active .desk-key { fill: #72e8d0; }
    #workstations > g.is-active .desk-screen-line { opacity: 1; stroke: #fff47b; }
    .station-label, .actor-label, .scene-label, .node-label, .lane-label { font-family: Inter, ui-sans-serif, system-ui, sans-serif; fill: #18344d; }
    .station-label { font-size: 18px; font-weight: 750; text-anchor: middle; }
    .actor-label { font-size: 16px; font-weight: 750; text-anchor: middle; }
    .scene-label { font-size: 22px; font-weight: 800; }
    .node-label { font-size: 15px; font-weight: 800; text-anchor: middle; }
    .lane-label { font-size: 22px; font-weight: 850; }
    .parcel-stamp { opacity: 0; transition: opacity .2s ease, transform .2s ease; }
    .parcel-stamp[data-visible="true"] { opacity: 1; }
    @media (prefers-reduced-motion: reduce) { .gnougo-actor, .task-object { transition-duration: .01ms !important; } }
  </style>
""");
    }

    private static void AppendScene(
        StringBuilder builder,
        AnimationSceneKind scene,
        int seed,
        int width,
        int height)
    {
        var (top, bottom, floor, accent) = scene switch
        {
            AnimationSceneKind.Meadow => ("#bce8ff", "#f8fdff", "#78c769", Palette(seed, ["#ff729f", "#a779e9", "#f7b32b", "#ef6461"])),
            AnimationSceneKind.Kitchen => ("#fff8ed", "#e9f3f2", "#d9c3a5", Palette(seed, ["#50a6c2", "#df795e", "#6c8ed4", "#65aa78"])),
            _ => ("#eef7fc", "#dcecf5", "#c7dce8", Palette(seed, ["#4f8ff7", "#7c69e8", "#2ca58d", "#e6804d"]))
        };

        builder.Append("  <g id=\"scene-").Append(scene.ToString().ToLowerInvariant())
            .Append("\" data-environment=\"").Append(scene.ToString().ToLowerInvariant()).AppendLine("\">");
        builder.Append("    <rect width=\"").Append(width).Append("\" height=\"").Append(height)
            .Append("\" fill=\"").Append(top).AppendLine("\"/>");
        builder.Append("    <rect y=\"110\" width=\"").Append(width).Append("\" height=\"").Append(Math.Max(0, height - 110))
            .Append("\" fill=\"").Append(bottom).AppendLine("\" opacity=\".9\"/>");
        builder.Append("    <path d=\"M0 ").Append(height - 170).Append("H").Append(width).Append("V").Append(height)
            .Append("H0Z\" fill=\"").Append(floor).AppendLine("\" opacity=\".78\"/>");
        builder.AppendLine("    <g id=\"scene-decorations\" opacity=\".7\">");
        for (var y = 360; y < height - 120; y += 620)
        {
            var left = 45 + (Math.Abs(seed + y) % 45);
            var right = width - 85 - (Math.Abs(seed * 3 + y) % 55);
            if (scene == AnimationSceneKind.Meadow)
            {
                builder.Append("      <g transform=\"translate(").Append(left).Append(' ').Append(y).Append(")\"><path d=\"M0 50q35-85 62 0q35-72 58 4q-6 42-59 47Q8 96 0 50Z\" fill=\"#49a957\"/><circle cx=\"20\" cy=\"112\" r=\"9\" fill=\"").Append(accent).AppendLine("\"/></g>");
                builder.Append("      <g transform=\"translate(").Append(right).Append(' ').Append(y + 130).Append(")\"><circle r=\"34\" fill=\"#fff\" opacity=\".72\"/><path d=\"M-16 5q16-26 32 0\" fill=\"none\" stroke=\"#315b73\" stroke-width=\"4\"/></g>").AppendLine();
            }
            else if (scene == AnimationSceneKind.Kitchen)
            {
                builder.Append("      <g transform=\"translate(").Append(left).Append(' ').Append(y).Append(")\"><rect width=\"105\" height=\"82\" rx=\"14\" fill=\"").Append(accent).Append("\"/><circle cx=\"28\" cy=\"28\" r=\"12\" fill=\"#f5d66e\"/><circle cx=\"72\" cy=\"28\" r=\"12\" fill=\"#f5d66e\"/></g>").AppendLine();
                builder.Append("      <g transform=\"translate(").Append(right).Append(' ').Append(y + 120).Append(")\"><path d=\"M0 45h92v72H0z\" fill=\"#8b5f45\"/><ellipse cx=\"46\" cy=\"35\" rx=\"39\" ry=\"14\" fill=\"#f2d37d\"/></g>").AppendLine();
            }
            else
            {
                builder.Append("      <g transform=\"translate(").Append(left).Append(' ').Append(y).Append(")\"><rect width=\"112\" height=\"76\" rx=\"14\" fill=\"#7890b9\"/><circle cx=\"30\" cy=\"38\" r=\"17\" fill=\"").Append(accent).Append("\"/><circle cx=\"78\" cy=\"38\" r=\"17\" fill=\"#f0b84b\"/></g>").AppendLine();
                builder.Append("      <g transform=\"translate(").Append(right).Append(' ').Append(y + 130).Append(")\"><path d=\"M0 55h78l-10 72H12z\" fill=\"#b86d3f\"/><path d=\"M39 57q-55-75-11-70q18-65 42-4q51-34 43 18q-4 43-74 56z\" fill=\"#45a56d\"/></g>").AppendLine();
            }
        }
        builder.AppendLine("    </g>");
        builder.AppendLine("  </g>");
    }

    private static void AppendOffice(StringBuilder builder, int seed)
    {
        var accent = Palette(seed, ["#4f8ff7", "#7c69e8", "#2ca58d", "#e6804d"]);
        builder.AppendLine($$"""
  <g id="scene-office" data-environment="office">
    <rect width="1600" height="900" fill="#eef7fc"/>
    <rect y="575" width="1600" height="325" fill="url(#scene-floor)"/>
    <path d="M0 575H1600" stroke="#86a9bf" stroke-width="5"/>
    <rect x="70" y="105" width="260" height="240" rx="18" fill="#c9edff" stroke="#527a94" stroke-width="8"/>
    <path d="M200 105V345M70 225H330" stroke="#fff" stroke-width="8"/>
    <g id="office-bookshelf" transform="translate(1420 90)">
      <rect width="125" height="390" rx="10" fill="#8d5d3e"/><path d="M0 95H125M0 190H125M0 285H125" stroke="#643d29" stroke-width="8"/>
      <g fill="{{accent}}"><rect x="14" y="23" width="18" height="65"/><rect x="39" y="34" width="25" height="54"/><rect x="73" y="18" width="15" height="70"/></g>
      <g fill="#f0b84b"><rect x="15" y="118" width="20" height="65"/><rect x="43" y="126" width="17" height="57"/><rect x="68" y="111" width="28" height="72"/></g>
    </g>
    <g id="office-sofa" transform="translate(70 690)"><rect width="300" height="125" rx="34" fill="#5e739d"/><rect x="25" y="-35" width="250" height="90" rx="28" fill="#7990b9"/><circle cx="82" cy="44" r="24" fill="{{accent}}"/><circle cx="220" cy="44" r="24" fill="#f0b84b"/></g>
    <g id="office-plants"><path d="M430 770h90l-12 85h-66z" fill="#b86d3f"/><path d="M475 774c-60-85-28-150 4-76 18-90 66-91 36-5 73-47 84 16 1 70z" fill="#45a56d"/></g>
    <g id="office-coffee" transform="translate(1020 690)"><rect width="180" height="18" rx="8" fill="#70452d"/><path d="M55 0v120M135 0v120" stroke="#70452d" stroke-width="12"/><rect x="72" y="-42" width="48" height="40" rx="9" fill="#fff" stroke="#8a4d39" stroke-width="5"/><path d="M120-32c25-4 25 25 0 22" fill="none" stroke="#8a4d39" stroke-width="5"/><path d="M83-53c-10-17 13-19 3-37M105-53c-10-17 13-19 3-37" fill="none" stroke="#789" stroke-width="4"/></g>
    <g id="office-lamps" fill="{{accent}}"><path d="M420 85h140l-34 55h-72z"/><path d="M850 85h140l-34 55h-72z"/><path d="M1250 85h140l-34 55h-72z"/></g>
  </g>
""");
    }

    private static void AppendMeadow(StringBuilder builder, int seed)
    {
        var flower = Palette(seed, ["#ff729f", "#a779e9", "#f7b32b", "#ef6461"]);
        builder.AppendLine($$"""
  <g id="scene-meadow" data-environment="meadow">
    <rect width="1600" height="900" fill="url(#scene-sky)"/>
    <path d="M0 560C240 480 410 610 650 535S1090 505 1600 545V900H0Z" fill="#8fd175"/>
    <path d="M0 670C300 580 520 730 850 635s510 30 750 5v260H0Z" fill="#65b75e"/>
    <g id="meadow-clouds" fill="#fff" opacity=".9"><path d="M120 150c35-70 120-55 132 4 69-30 119 74 38 93H105c-64-9-50-87 15-97z"/><path d="M1130 115c31-58 102-43 116 4 57-18 94 64 29 82h-172c-55-12-28-78 27-86z"/></g>
    <g id="meadow-tree" transform="translate(1420 260)"><path d="M58 170v290" stroke="#7a4a2d" stroke-width="45"/><circle cx="60" cy="125" r="112" fill="#4eaa5b"/><circle cx="0" cy="170" r="78" fill="#61bd68"/><circle cx="128" cy="172" r="74" fill="#39964d"/></g>
    <g id="meadow-picnic" transform="translate(90 690)"><path d="M0 70l280-38 40 125-287 31z" fill="#fff4dc" stroke="#d65454" stroke-width="9"/><path d="M72 61l31 112M150 50l31 112M230 40l31 112" stroke="#d65454" stroke-width="8"/><circle cx="252" cy="48" r="27" fill="#f0b84b"/><rect x="48" y="37" width="54" height="42" rx="9" fill="#6b9bd2"/></g>
    <g id="meadow-rocks" fill="#879aa2"><ellipse cx="510" cy="760" rx="70" ry="33"/><ellipse cx="590" cy="785" rx="44" ry="24"/><ellipse cx="1210" cy="745" rx="66" ry="30"/></g>
    <g id="meadow-flowers" fill="{{flower}}"><circle cx="420" cy="590" r="12"/><circle cx="470" cy="650" r="10"/><circle cx="730" cy="590" r="12"/><circle cx="990" cy="690" r="11"/><circle cx="1320" cy="620" r="13"/><circle cx="1140" cy="785" r="10"/></g>
    <g id="meadow-birds" fill="none" stroke="#315b73" stroke-width="5"><path d="M710 110q18-22 36 0 18-22 36 0"/><path d="M900 155q15-18 30 0 15-18 30 0"/></g>
  </g>
""");
    }

    private static void AppendKitchen(StringBuilder builder, int seed)
    {
        var accent = Palette(seed, ["#50a6c2", "#df795e", "#6c8ed4", "#65aa78"]);
        builder.AppendLine($$"""
  <g id="scene-kitchen" data-environment="kitchen">
    <rect width="1600" height="900" fill="#fff8ed"/>
    <path d="M0 110H1600V560H0Z" fill="#e9f3f2"/>
    <g id="kitchen-tiles" stroke="#c5dcda" stroke-width="3" opacity=".7"><path d="M0 220H1600M0 330H1600M0 440H1600M100 110V560M300 110V560M500 110V560M700 110V560M900 110V560M1100 110V560M1300 110V560M1500 110V560"/></g>
    <rect y="560" width="1600" height="340" fill="#d9c3a5"/><path d="M0 670H1600M0 780H1600" stroke="#c0a987" stroke-width="4"/>
    <g id="kitchen-cabinets" transform="translate(65 135)"><rect width="390" height="155" rx="12" fill="{{accent}}"/><path d="M195 0v155" stroke="#33596d" stroke-width="5"/><circle cx="175" cy="78" r="8" fill="#f7d176"/><circle cx="215" cy="78" r="8" fill="#f7d176"/></g>
    <g id="kitchen-fridge" transform="translate(1390 180)"><rect width="155" height="390" rx="18" fill="#dce9ed" stroke="#7897a1" stroke-width="8"/><path d="M0 170h155" stroke="#7897a1" stroke-width="6"/><path d="M25 50v72M25 218v90" stroke="#7897a1" stroke-width="10" stroke-linecap="round"/><rect x="90" y="55" width="42" height="52" rx="5" fill="#fff3a8"/></g>
    <g id="kitchen-stove" transform="translate(1070 455)"><rect width="245" height="130" rx="12" fill="#586a73"/><circle cx="55" cy="28" r="22" fill="#26343c"/><circle cx="125" cy="28" r="22" fill="#26343c"/><circle cx="195" cy="28" r="22" fill="#26343c"/><rect x="45" y="63" width="155" height="52" rx="8" fill="#18242a"/><circle cx="223" cy="80" r="10" fill="#f7b32b"/></g>
    <g id="kitchen-island" transform="translate(620 650)"><rect width="380" height="38" rx="14" fill="#8b5f45"/><path d="M45 35v180M335 35v180" stroke="#78513c" stroke-width="22"/><ellipse cx="110" cy="-8" rx="65" ry="22" fill="#f2d37d"/><circle cx="90" cy="-24" r="15" fill="#e85e54"/><circle cx="125" cy="-28" r="17" fill="#69a84f"/><circle cx="151" cy="-16" r="13" fill="#f29b38"/></g>
    <g id="kitchen-utensils" stroke="#5c6d73" stroke-width="8" stroke-linecap="round"><path d="M540 160v120M585 160v120M630 160v120"/><circle cx="540" cy="295" r="20" fill="none"/><path d="M570 282h30M615 280l30 20"/></g>
    <g id="kitchen-pots" fill="#82979e"><ellipse cx="1180" cy="385" rx="66" ry="22"/><path d="M1120 385v55q60 35 120 0v-55z"/><path d="M1240 405h55" stroke="#82979e" stroke-width="15"/></g>
  </g>
""");
    }

    private static void AppendLanes(StringBuilder builder, IReadOnlyList<AnimationWorkflowLane> lanes)
    {
        builder.AppendLine("  <g id=\"workflow-lanes\" aria-label=\"Workflow swimlanes\">");
        foreach (var lane in lanes)
        {
            var top = Math.Max(120, lane.StartY - 90);
            var height = Math.Max(260, lane.EndY - top + 190);
            builder.Append("    <g id=\"").Append(lane.Id).Append("\" data-workflow-instance=\"")
                .Append(EscapeAttribute(lane.WorkflowInstanceId)).Append("\" data-workflow=\"")
                .Append(EscapeAttribute(lane.WorkflowName)).AppendLine("\">");
            builder.Append("      <rect class=\"flow-lane\" x=\"").Append(Number(lane.X - lane.Width / 2))
                .Append("\" y=\"").Append(Number(top)).Append("\" width=\"").Append(Number(lane.Width))
                .Append("\" height=\"").Append(Number(height)).AppendLine("\" rx=\"34\"/>");
            builder.Append("      <rect x=\"").Append(Number(lane.X - lane.Width / 2 + 18)).Append("\" y=\"")
                .Append(Number(top + 16)).Append("\" width=\"").Append(Number(lane.Width - 36))
                .AppendLine("\" height=\"52\" rx=\"20\" fill=\"#fff\" opacity=\".9\"/>");
            builder.Append("      <text class=\"lane-label\" x=\"").Append(Number(lane.X - lane.Width / 2 + 40))
                .Append("\" y=\"").Append(Number(top + 50)).Append("\">")
                .Append(Escape(lane.Label)).AppendLine("</text>");
            builder.AppendLine("    </g>");
        }
        builder.AppendLine("  </g>");
    }

    private static void AppendHeader(StringBuilder builder, GnouGnouAnimationPlan plan, int canvasWidth)
    {
        var width = Math.Max(600, canvasWidth - 84);
        builder.AppendLine("  <g id=\"scene-header\" transform=\"translate(42 34)\">");
        builder.Append("    <rect width=\"").Append(width).AppendLine("\" height=\"64\" rx=\"22\" fill=\"#ffffff\" opacity=\".94\" filter=\"url(#scene-shadow)\"/>");
        builder.Append("    <text class=\"scene-label\" x=\"28\" y=\"40\">GnOuGo · ").Append(Escape(plan.Entrypoint)).AppendLine("</text>");
        builder.Append("    <text id=\"simulation-status\" class=\"station-label\" text-anchor=\"end\" x=\"").Append(width - 30)
            .Append("\" y=\"40\">Ready · seed ")
            .Append(plan.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine("</text>");
        builder.AppendLine("  </g>");
    }

    private static void AppendEdges(
        StringBuilder builder,
        IReadOnlyList<AnimationFlowEdge> edges,
        IReadOnlyList<AnimationFlowNode> nodes)
    {
        var byId = nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        builder.AppendLine("  <g id=\"workflow-edges\" aria-label=\"Workflow connections\">");
        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            var edge = edges[edgeIndex];
            if (!byId.TryGetValue(edge.FromNodeId, out var from) || !byId.TryGetValue(edge.ToNodeId, out var to))
                continue;
            var startY = from.Position.Y + 108;
            var endY = to.Position.Y + 108;
            var middleY = startY + (endY - startY) / 2;
            var bend = (edgeIndex % 2 == 0 ? 1 : -1) * (32 + edgeIndex % 3 * 9);
            string path;
            if (Math.Abs(endY - startY) < 20)
            {
                var deltaX = to.Position.X - from.Position.X;
                path = $"M {Number(from.Position.X)} {Number(startY)} C {Number(from.Position.X + deltaX * .34)} {Number(startY - bend)} {Number(to.Position.X - deltaX * .34)} {Number(endY + bend)} {Number(to.Position.X)} {Number(endY)}";
            }
            else if (Math.Abs(from.Position.X - to.Position.X) < 1)
            {
                path = $"M {Number(from.Position.X)} {Number(startY)} C {Number(from.Position.X + bend)} {Number(startY + (endY - startY) * .3)} {Number(to.Position.X - bend)} {Number(startY + (endY - startY) * .72)} {Number(to.Position.X)} {Number(endY)}";
            }
            else
            {
                path = $"M {Number(from.Position.X)} {Number(startY)} C {Number(from.Position.X + bend)} {Number(middleY)} {Number(to.Position.X - bend)} {Number(middleY)} {Number(to.Position.X)} {Number(endY)}";
            }

            builder.Append("    <g id=\"").Append(edge.Id).Append("\" class=\"flow-edge\" data-edge-kind=\"")
                .Append(edge.Kind.ToString().ToLowerInvariant()).Append("\" data-selected=\"")
                .Append(edge.IsSelected ? "true" : "false").AppendLine("\">");
            builder.Append("      <path class=\"route-shadow\" d=\"").Append(path).AppendLine("\"/>");
            builder.Append("      <path class=\"route-bed\" d=\"").Append(path).AppendLine("\" filter=\"url(#route-rough)\"/>");
            builder.Append("      <path class=\"route-surface\" data-route-path=\"true\" d=\"").Append(path).AppendLine("\" filter=\"url(#route-rough)\"/>");
            builder.Append("      <path class=\"route-ruts\" d=\"").Append(path).AppendLine("\"/>");
            for (var stoneIndex = 0; stoneIndex < 4; stoneIndex++)
            {
                var progress = (stoneIndex + 1d) / 5d;
                var side = (stoneIndex + edgeIndex) % 2 == 0 ? -1 : 1;
                var stoneX = from.Position.X + (to.Position.X - from.Position.X) * progress + side * (25 + stoneIndex % 2 * 5);
                var stoneY = startY + (endY - startY) * progress + (stoneIndex % 2 == 0 ? -5 : 7);
                var radiusX = 7 + (edgeIndex + stoneIndex) % 5;
                var radiusY = 4 + (edgeIndex + stoneIndex * 2) % 4;
                builder.Append("      <ellipse class=\"route-stone\" cx=\"").Append(Number(stoneX))
                    .Append("\" cy=\"").Append(Number(stoneY)).Append("\" rx=\"").Append(radiusX)
                    .Append("\" ry=\"").Append(radiusY).Append("\" transform=\"rotate(")
                    .Append((edgeIndex * 19 + stoneIndex * 31) % 70 - 35).Append(' ')
                    .Append(Number(stoneX)).Append(' ').Append(Number(stoneY)).AppendLine(")\"/>");
            }
            builder.AppendLine("    </g>");
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append("    <text class=\"node-label\" x=\"").Append(Number((from.Position.X + to.Position.X) / 2))
                    .Append("\" y=\"").Append(Number(middleY - 10)).Append("\" opacity=\".76\">")
                    .Append(Escape(edge.Label)).AppendLine("</text>");
            }
        }
        builder.AppendLine("  </g>");
    }

    private static void AppendFlowNodes(StringBuilder builder, IReadOnlyList<AnimationFlowNode> nodes)
    {
        builder.AppendLine("  <g id=\"workflow-nodes\" aria-label=\"Workflow control nodes\">");
        foreach (var node in nodes.Where(static item =>
                     item.Kind is not AnimationFlowNodeKind.Desk
                         and not AnimationFlowNodeKind.WorkflowCall
                         and not AnimationFlowNodeKind.Delivery))
        {
            builder.Append("    <g id=\"").Append(node.Id).Append("\" class=\"flow-node\" data-node-kind=\"")
                .Append(node.Kind.ToString().ToLowerInvariant()).Append("\" data-selected=\"")
                .Append(node.IsSelected ? "true" : "false").Append("\" data-step-id=\"")
                .Append(EscapeAttribute(node.StepId ?? "")).Append("\" transform=\"translate(")
                .Append(Number(node.Position.X)).Append(' ').Append(Number(node.Position.Y)).AppendLine(")\">");
            var glyph = node.Kind switch
            {
                AnimationFlowNodeKind.Start => "▶",
                AnimationFlowNodeKind.Finish => "✓",
                AnimationFlowNodeKind.Fork => "⇶",
                AnimationFlowNodeKind.Join => "⇥",
                AnimationFlowNodeKind.Decision => "?",
                AnimationFlowNodeKind.Loop => "↻",
                AnimationFlowNodeKind.Return => "↩",
                _ => "•"
            };
            builder.AppendLine("      <g class=\"isometric-sign\" filter=\"url(#scene-shadow)\">");
            builder.AppendLine("        <path class=\"sign-post\" d=\"M-9 10l16 5v67L-9 77z\"/>");
            builder.AppendLine("        <path class=\"sign-post\" d=\"M-31 83l25-12 35 11-25 13z\"/>");
            builder.AppendLine("        <path class=\"sign-face\" d=\"M-76-52H52V9H-76Z\"/>");
            builder.AppendLine("        <path class=\"sign-top\" d=\"M-76-52l19-15H72L52-52z\"/>");
            builder.AppendLine("        <path class=\"sign-side\" d=\"M52-52l20-15v61L52 9z\"/>");
            builder.Append("        <text class=\"sign-glyph\" x=\"-12\" y=\"-10\">").Append(glyph).AppendLine("</text>");
            builder.AppendLine("      </g>");
            builder.Append("      <text class=\"node-label\" x=\"-2\" y=\"118\">").Append(Escape(node.Label)).AppendLine("</text>");
            builder.AppendLine("    </g>");
        }
        builder.AppendLine("  </g>");
    }

    private static void AppendStations(
        StringBuilder builder,
        IReadOnlyList<AnimationStation> stations,
        IReadOnlyList<AnimationFlowNode> nodes)
    {
        var nodesByStation = nodes
            .Where(static node => node.StationId is not null)
            .ToDictionary(static node => node.StationId!, StringComparer.Ordinal);
        builder.AppendLine("  <g id=\"workstations\" aria-label=\"Workflow workstations\">");
        foreach (var station in stations)
        {
            if (!nodesByStation.TryGetValue(station.Id, out var node))
                continue;
            builder.Append("    <g id=\"").Append(station.Id).Append("\" class=\"workflow-station\" data-node-kind=\"")
                .Append(node.Kind.ToString().ToLowerInvariant()).Append("\" data-station-kind=\"")
                .Append(station.Kind.ToString().ToLowerInvariant()).Append("\" data-node-id=\"").Append(node.Id)
                .Append("\" data-workflow-instance-id=\"").Append(EscapeAttribute(station.WorkflowInstanceId ?? ""))
                .Append("\" data-step-id=\"").Append(EscapeAttribute(station.StepId ?? ""))
                .Append("\" data-step-type=\"").Append(EscapeAttribute(station.StepType ?? ""))
                .Append("\" transform=\"translate(").Append(Number(node.Position.X)).Append(' ')
                .Append(Number(node.Position.Y)).AppendLine(")\">");
            if (station.Kind == AnimationStationKind.DeliveryDock)
            {
                builder.AppendLine("      <g class=\"isometric-delivery\" filter=\"url(#scene-shadow)\">");
                builder.AppendLine("        <path class=\"desk-shadow\" d=\"M-122 42L20-9l129 47L4 91z\"/>");
                builder.AppendLine("        <path class=\"desk-front\" d=\"M-108 24L6 65l129-45v31L6 98l-114-42z\"/>");
                builder.AppendLine("        <path class=\"desk-top\" d=\"M-108 24L18-20l117 40L6 65z\"/>");
                builder.AppendLine("        <path d=\"M-36 26l45 16 52-18M11 41v-54M-9 3L11-13 31 2\" fill=\"none\" stroke=\"#4f8ff7\" stroke-width=\"8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                builder.AppendLine("      </g>");
                builder.AppendLine("      <text class=\"station-label\" x=\"8\" y=\"126\">Isometric delivery dock</text>");
            }
            else if (station.Kind == AnimationStationKind.HandoffDesk)
            {
                builder.AppendLine("      <g class=\"isometric-desk isometric-handoff\" filter=\"url(#scene-shadow)\">");
                builder.AppendLine("        <path class=\"desk-shadow\" d=\"M-132 52L25-5l125 45L-8 99z\"/>");
                builder.AppendLine("        <path class=\"desk-leg\" d=\"M-102 46l18 7v70l-18-7zM105 31l17-6v65l-17 6z\"/>");
                builder.AppendLine("        <path class=\"desk-front\" d=\"M-123 30L-36 62 138 3v18L-36 82l-87-33z\"/>");
                builder.AppendLine("        <path class=\"desk-side\" d=\"M-123 30L45-31l93 34L-36 62z\"/>");
                builder.AppendLine("        <path class=\"desk-top\" d=\"M-123 22L45-39l93 34L-36 55z\"/>");
                builder.AppendLine("        <path d=\"M-69 20l52 18-17 8M-17 38l-18-20M70-17L18 2l17 8M18 2l18-20\" fill=\"none\" stroke=\"#28a9a0\" stroke-width=\"7\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                builder.AppendLine("      </g>");
                builder.Append("      <text class=\"station-label\" x=\"5\" y=\"118\">").Append(Escape(station.Label)).AppendLine("</text>");
            }
            else
            {
                builder.AppendLine("      <g class=\"isometric-desk\" filter=\"url(#scene-shadow)\">");
                builder.AppendLine("        <path class=\"desk-shadow\" d=\"M-137 61L18 5l143 51L4 113z\"/>");
                builder.AppendLine("        <path class=\"desk-leg\" d=\"M-108 55l18 7v70l-18-7zM111 39l18-6v69l-18 6zM-27 82l17 6v54l-17-6z\"/>");
                builder.AppendLine("        <path class=\"desk-front\" d=\"M-126 33L-38 66 143 3v20L-38 87l-88-34z\"/>");
                builder.AppendLine("        <path class=\"desk-side\" d=\"M-126 33L46-29l97 32L-38 66z\"/>");
                builder.AppendLine("        <path class=\"desk-top\" d=\"M-126 24L46-38l97 32L-38 57z\"/>");
                builder.AppendLine("        <g class=\"isometric-laptop\">");
                builder.AppendLine("          <path class=\"laptop-shell\" d=\"M-50-53L39-25V34L-50 6Z\"/>");
                builder.AppendLine("          <path class=\"laptop-side\" d=\"M39-25l8-4v59l-8 4z\"/>");
                builder.AppendLine("          <path class=\"laptop-screen\" d=\"M-42-43L31-20V22L-42-1Z\"/>");
                builder.AppendLine("          <path class=\"desk-screen-line\" d=\"M-30-28l42 13M-30-16L22 1M-30-4l33 10\"/>");
                builder.AppendLine("          <path class=\"laptop-hinge\" d=\"M-45 5l80 25\"/>");
                builder.AppendLine("          <path class=\"laptop-shell\" d=\"M-50 6l89 28 42-14-91-29z\"/>");
                builder.AppendLine("          <path class=\"laptop-base-front\" d=\"M-50 6l89 28 42-14v7L39 41-50 13z\"/>");
                builder.AppendLine("          <g data-part=\"keyboard\">");
                for (var row = 0; row < 3; row++)
                {
                    for (var column = 0; column < 6; column++)
                    {
                        builder.Append("            <rect class=\"desk-key\" data-key=\"").Append(row * 6 + column)
                            .Append("\" x=\"").Append(-25 + column * 11).Append("\" y=\"").Append(5 + row * 7)
                            .AppendLine("\" width=\"8\" height=\"4\" rx=\"1.5\" transform=\"rotate(17 0 16)\"/>");
                    }
                }
                builder.AppendLine("          </g>");
                builder.AppendLine("          <path class=\"laptop-trackpad\" d=\"M12 19l21 7 14-5-22-7z\"/>");
                builder.AppendLine("        </g>");
                builder.AppendLine("        <g class=\"isometric-chair\" transform=\"translate(62 83)\">");
                builder.AppendLine("          <path d=\"M-37-7l42-15 39 14L2 8z\" fill=\"#567b99\" stroke=\"#29475e\" stroke-width=\"4\"/>");
                builder.AppendLine("          <path d=\"M5-22v-45l39 14v45\" fill=\"#6f96b4\" stroke=\"#29475e\" stroke-width=\"4\"/>");
                builder.AppendLine("          <path d=\"M-13 3l-5 45M28 4l8 44\" stroke=\"#29475e\" stroke-width=\"6\"/>");
                builder.AppendLine("      </g>");
                builder.AppendLine("      </g>");
                builder.AppendLine("      <g class=\"step-plaque\" transform=\"translate(-151 61)\">");
                builder.AppendLine("        <path d=\"M-6-28h112l16 12v52H-6z\" fill=\"#fff\" stroke=\"#315f82\" stroke-width=\"3\"/>");
                builder.AppendLine("        <path d=\"M106-28l16 12H106z\" fill=\"#b9d6e8\" stroke=\"#315f82\" stroke-width=\"3\"/>");
                builder.Append("        <text class=\"station-label\" x=\"50\" y=\"0\">").Append(Escape(station.Label)).AppendLine("</text>");
                builder.Append("        <text class=\"node-label\" x=\"50\" y=\"22\" opacity=\".68\">")
                    .Append(Escape(station.StepType ?? "step")).AppendLine("</text>");
                builder.AppendLine("      </g>");
            }
            builder.AppendLine("    </g>");
        }
        builder.AppendLine("  </g>");
    }

    private static void AppendTasks(StringBuilder builder, GnouGnouAnimationPlan plan)
    {
        var scheduledProgress = plan.Events
            .Select(static item => item.ProgressTotal ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var totalProgress = Math.Max(
            scheduledProgress,
            plan.Stations.Count(static station =>
                station.Kind is AnimationStationKind.KeyboardDesk or AnimationStationKind.Human));
        builder.AppendLine("  <g id=\"task-objects\" aria-label=\"Animated task objects\">");
        foreach (var task in plan.Tasks)
        {
            var initialActor = plan.Actors.FirstOrDefault(actor => actor.Id == task.InitialActorId);
            var position = task.InitiallyVisible
                ? new AnimationPoint(initialActor?.Home.X ?? 780, -55)
                : initialActor?.Home ?? new AnimationPoint(780, 690);
            builder.Append("    <g id=\"").Append(task.Id).Append("\" class=\"task-object\" data-visible=\"")
                .Append(task.InitiallyVisible ? "true" : "false").Append("\" data-task-kind=\"").Append(EscapeAttribute(task.Kind))
                .Append("\" data-workflow=\"").Append(EscapeAttribute(task.WorkflowName ?? ""))
                .Append("\" data-step-id=\"").Append(EscapeAttribute(task.StepId ?? ""))
                .Append("\" transform=\"translate(").Append(Number(position.X)).Append(' ').Append(Number(position.Y)).AppendLine(")\">");
            AppendTaskShape(builder, task, totalProgress);
            builder.Append("      <title>").Append(Escape(task.Label)).AppendLine("</title>");
            builder.AppendLine("    </g>");
        }
        builder.AppendLine("  </g>");
    }

    private static void AppendTaskShape(StringBuilder builder, AnimationTaskObject task, int totalProgress)
    {
        if (task.Kind == "project-parcel")
        {
            builder.AppendLine("      <circle class=\"task-aura\" r=\"52\" fill=\"#f4bc45\"/>");
            builder.AppendLine("      <g data-part=\"parcel-body\">");
            builder.AppendLine("        <rect class=\"task-body\" x=\"-43\" y=\"-34\" width=\"86\" height=\"68\" rx=\"14\" fill=\"#e5a94c\" stroke=\"#7e4e1f\" stroke-width=\"5\" filter=\"url(#scene-shadow)\"/>");
            builder.AppendLine("        <path d=\"M-43-9H43M0-34V34\" fill=\"none\" stroke=\"#fff0b5\" stroke-width=\"7\" opacity=\".85\"/>");
            builder.AppendLine("        <path d=\"M-17-34v-12h34v12\" fill=\"none\" stroke=\"#7e4e1f\" stroke-width=\"5\"/>");
            builder.AppendLine("        <rect x=\"-26\" y=\"-4\" width=\"52\" height=\"26\" rx=\"8\" fill=\"#fff8d8\" stroke=\"#8e5f19\" stroke-width=\"2\"/>");
            builder.AppendLine("        <text class=\"task-glyph\" x=\"0\" y=\"10\" font-size=\"18\">G</text>");
            builder.AppendLine("      </g>");
            builder.AppendLine("      <g data-part=\"parcel-stamps\">");
            var stampCount = Math.Min(12, Math.Max(1, totalProgress));
            for (var index = 0; index < stampCount; index++)
            {
                var column = index % 6;
                var row = index / 6;
                var x = -35 + column * 14;
                var y = 42 + row * 14;
                builder.Append("        <circle class=\"parcel-stamp\" data-stamp-index=\"").Append(index + 1)
                    .Append("\" data-visible=\"false\" cx=\"").Append(x).Append("\" cy=\"").Append(y)
                    .Append("\" r=\"5\" fill=\"").Append(index % 2 == 0 ? "#42c97b" : "#4f8ff7")
                    .AppendLine("\" stroke=\"#fff\" stroke-width=\"2\"/>");
            }
            builder.AppendLine("      </g>");
            builder.AppendLine("      <rect x=\"-62\" y=\"68\" width=\"124\" height=\"26\" rx=\"13\" fill=\"#fff\" opacity=\".95\" stroke=\"#31516a\" stroke-width=\"1.5\"/>");
            builder.AppendLine("      <text class=\"task-label\" data-part=\"parcel-progress\" x=\"0\" y=\"85\">Project parcel · 0%</text>");
            return;
        }

        var (color, outline, glyph) = task.Kind switch
        {
            "input" => ("#f4bc45", "#8e5f19", "↓"),
            "branch" => ("#24cbb5", "#116d67", "◇"),
            _ when task.Kind.StartsWith("llm.", StringComparison.Ordinal) => ("#8c68e8", "#4e329f", "✦"),
            _ when task.Kind.StartsWith("mcp.", StringComparison.Ordinal) => ("#398bd7", "#19518d", "⌁"),
            _ when task.Kind.StartsWith("human.", StringComparison.Ordinal) => ("#ee7f74", "#9c3d3a", "☺"),
            _ when task.Kind.StartsWith("workflow.", StringComparison.Ordinal) => ("#28a9a0", "#126c69", "⇄"),
            _ when task.Kind.StartsWith("template.", StringComparison.Ordinal) || task.Kind is "set" or "emit" => ("#e99443", "#93531c", "✎"),
            _ => ("#637f9d", "#314c66", "⚒")
        };
        var label = task.Label.Length <= 22 ? task.Label : string.Concat(task.Label.AsSpan(0, 19), "…");

        builder.Append("      <circle class=\"task-aura\" r=\"39\" fill=\"").Append(color).AppendLine("\"/>");
        builder.Append("      <rect class=\"task-body\" x=\"-28\" y=\"-28\" width=\"56\" height=\"56\" rx=\"15\" fill=\"")
            .Append(color).Append("\" stroke=\"").Append(outline).AppendLine("\" stroke-width=\"5\" filter=\"url(#scene-shadow)\"/>");
        builder.Append("      <text class=\"task-glyph\" x=\"0\" y=\"1\">").Append(glyph).AppendLine("</text>");
        builder.AppendLine("      <rect x=\"-54\" y=\"34\" width=\"108\" height=\"24\" rx=\"12\" fill=\"#fff\" opacity=\".94\" stroke=\"#31516a\" stroke-width=\"1.5\"/>");
        builder.Append("      <text class=\"task-label\" x=\"0\" y=\"50\">").Append(Escape(label)).AppendLine("</text>");
    }

    private static void AppendActors(StringBuilder builder, GnouGnouAnimationPlan plan)
    {
        builder.AppendLine("  <g id=\"gnougo-team\" aria-label=\"GnOuGo workflow team\">");
        foreach (var actor in plan.Actors)
        {
            var parent = actor.CloneOfActorId is null ? null : plan.Actors.FirstOrDefault(item => item.Id == actor.CloneOfActorId);
            var masterAppearance = actor.Kind == AnimationActorKind.Master || parent?.Kind == AnimationActorKind.Master;
            var lane = plan.Lanes.FirstOrDefault(item =>
                item.ActorId == actor.Id || actor.CloneOfActorId is not null && item.ActorId == actor.CloneOfActorId);
            var bear = CreateBear(actor, masterAppearance);
            builder.Append("    <g id=\"").Append(actor.Id).Append("\" class=\"gnougo-actor")
                .Append(actor.Kind == AnimationActorKind.Clone ? " is-clone" : "")
                .Append("\" data-visible=\"").Append(actor.InitiallyVisible ? "true" : "false")
                .Append("\" data-actor-kind=\"").Append(actor.Kind.ToString().ToLowerInvariant())
                .Append("\" data-bearded=\"").Append(masterAppearance ? "true" : "false")
                .Append("\" data-visual-seed=\"").Append(actor.VisualSeed.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-lane-id=\"").Append(EscapeAttribute(lane?.Id ?? ""))
                .Append("\" data-workflow=\"").Append(EscapeAttribute(actor.WorkflowName))
                .Append("\" transform=\"translate(").Append(Number(actor.Home.X)).Append(' ').Append(Number(actor.Home.Y)).AppendLine(")\">");
            builder.Append("      ").AppendLine(IndentNested(bear, 6));
            builder.AppendLine("      <rect x=\"-86\" y=\"10\" width=\"172\" height=\"30\" rx=\"15\" fill=\"#fff\" opacity=\".94\" stroke=\"#264967\" stroke-width=\"2\"/>");
            builder.Append("      <text class=\"actor-label\" x=\"0\" y=\"31\">").Append(Escape(actor.Label)).AppendLine("</text>");
            builder.AppendLine("    </g>");
        }
        builder.AppendLine("  </g>");
    }

    private static string CreateBear(AnimationActor actor, bool masterAppearance)
    {
        var unsigned = unchecked((uint)actor.VisualSeed);
        var palettes = Enum.GetValues<GnouGnouBearFurPalette>();
        var accessories = new[]
        {
            GnouGnouBearAccessory.Notebook,
            GnouGnouBearAccessory.Laptop,
            GnouGnouBearAccessory.CoffeeMug,
            GnouGnouBearAccessory.Pencil,
            GnouGnouBearAccessory.Magnifier
        };
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Seed = actor.VisualSeed,
            Size = 256,
            SvgIdPrefix = actor.Id,
            Theme = GnouGnouBearTheme.Transparent,
            Role = masterAppearance ? GnouGnouBearRole.Planner : GnouGnouBearRole.Coder,
            Emotion = masterAppearance ? GnouGnouBearEmotion.Proud : GnouGnouBearEmotion.Focused,
            State = GnouGnouBearState.Idle,
            FurPalette = palettes[(int)(unsigned % (uint)palettes.Length)],
            Accessory = accessories[(int)((unsigned / 7u) % (uint)accessories.Length)],
            AccessoryColorVariant = (int)((unsigned / 13u) % 5u),
            EyeStyle = masterAppearance ? GnouGnouBearEyeStyle.BigGlossy : GnouGnouBearEyeStyle.Default,
            HasBeard = masterAppearance,
            HasHeadphones = true,
            HasBowTie = masterAppearance,
            EnableAnimationRig = true,
            Title = actor.Label,
            Description = $"{actor.Kind} GnOuGo for workflow {actor.WorkflowName}."
        });

        return svg.Replace(
            "<svg width=\"256\" height=\"256\"",
            "<svg x=\"-90\" y=\"-180\" width=\"180\" height=\"180\"",
            StringComparison.Ordinal);
    }

    private static string IndentNested(string value, int spaces)
    {
        var indent = new string(' ', spaces);
        return value.Replace("\n", $"\n{indent}", StringComparison.Ordinal);
    }

    private static string Palette(int seed, string[] colors)
    {
        var index = (int)(unchecked((uint)seed) % (uint)colors.Length);
        return colors[index];
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value) => Escape(value);
}
