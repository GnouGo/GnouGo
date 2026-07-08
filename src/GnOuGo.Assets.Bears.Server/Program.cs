using System.Security.Cryptography;
using System.Text;
using GnOuGo.Assets.Bears;

var builder = WebApplication.CreateSlimBuilder(args);

var app = builder.Build();

app.MapMethods("/", ["GET"], (RequestDelegate)(async context =>
{
    var options = CreateRandomBearOptions();
    var svg = GnouGnouBearSvgGenerator.Generate(options);
    var html = RenderPage(options, svg);

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
}));

app.MapMethods("/bear.svg", ["GET"], (RequestDelegate)(async context =>
{
    var seed = TryGetSeed(context);
    var options = CreateRandomBearOptions(seed);
    var svg = GnouGnouBearSvgGenerator.Generate(options);

    context.Response.ContentType = "image/svg+xml; charset=utf-8";
    await context.Response.WriteAsync(svg);
}));

app.Run();

static GnouGnouBearOptions CreateRandomBearOptions(int? seed = null)
{
    var actualSeed = seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);

    return new GnouGnouBearOptions
    {
        Seed = actualSeed,
        Role = Pick<GnouGnouBearRole>(),
        Emotion = Pick<GnouGnouBearEmotion>(),
        Accessory = Pick<GnouGnouBearAccessory>(),
        State = Pick<GnouGnouBearState>(),
        Theme = Pick<GnouGnouBearTheme>(),
        FurPalette = Pick<GnouGnouBearFurPalette>(),
        EyeStyle = Pick<GnouGnouBearEyeStyle>(),
        HasHeadphones = RandomBool(percentTrue: 55),
        HasBowTie = RandomBool(percentTrue: 55),
        Size = 512,
        Title = "GnouGnou",
        Description = $"Random deterministic GnouGnou SVG mascot generated from seed {actualSeed}."
    };
}

static T Pick<T>()
    where T : struct, Enum
{
    var values = Enum.GetValues<T>();
    return values[RandomNumberGenerator.GetInt32(values.Length)];
}

static bool RandomBool(int percentTrue)
{
    return RandomNumberGenerator.GetInt32(100) < percentTrue;
}

static int? TryGetSeed(HttpContext context)
{
    return int.TryParse(context.Request.Query["seed"], out var seed) ? seed : null;
}

static string RenderPage(GnouGnouBearOptions options, string svg)
{
    var builder = new StringBuilder();
    builder.AppendLine("<!doctype html>");
    builder.AppendLine("<html lang=\"en\">");
    builder.AppendLine("<head>");
    builder.AppendLine("  <meta charset=\"utf-8\">");
    builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
    builder.AppendLine("  <meta http-equiv=\"cache-control\" content=\"no-store\">");
    builder.AppendLine("  <title>GnouGnou Bear Generator</title>");
    builder.AppendLine("  <style>");
    builder.AppendLine("    :root { color-scheme: light; font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif; background: #f6fbff; color: #1d2838; }");
    builder.AppendLine("    * { box-sizing: border-box; }");
    builder.AppendLine("    body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px; }");
    builder.AppendLine("    main { width: min(920px, 100%); display: grid; grid-template-columns: minmax(260px, 520px) minmax(220px, 1fr); gap: 28px; align-items: center; }");
    builder.AppendLine("    .preview { display: grid; place-items: center; }");
    builder.AppendLine("    .preview svg { width: min(512px, 100%); height: auto; display: block; }");
    builder.AppendLine("    .details { display: grid; gap: 18px; }");
    builder.AppendLine("    h1 { margin: 0; font-size: clamp(2rem, 4vw, 4rem); line-height: 0.95; color: #173b6d; }");
    builder.AppendLine("    p { margin: 0; font-size: 1rem; line-height: 1.6; color: #42526a; }");
    builder.AppendLine("    dl { margin: 0; display: grid; grid-template-columns: auto 1fr; gap: 10px 14px; font-size: 0.96rem; }");
    builder.AppendLine("    dt { color: #617086; }");
    builder.AppendLine("    dd { margin: 0; font-weight: 700; color: #1d2838; }");
    builder.AppendLine("    a { width: fit-content; display: inline-flex; align-items: center; justify-content: center; min-height: 42px; padding: 0 16px; border-radius: 8px; background: #245aa6; color: #fff; font-weight: 700; text-decoration: none; }");
    builder.AppendLine("    @media (max-width: 760px) { body { place-items: start center; } main { grid-template-columns: 1fr; } h1 { font-size: 2.4rem; } }");
    builder.AppendLine("  </style>");
    builder.AppendLine("</head>");
    builder.AppendLine("<body>");
    builder.AppendLine("  <main>");
    builder.AppendLine("    <section class=\"preview\" aria-label=\"Generated GnouGnou mascot\">");
    builder.AppendLine(svg);
    builder.AppendLine("    </section>");
    builder.AppendLine("    <section class=\"details\">");
    builder.AppendLine("      <h1>GnouGnou</h1>");
    builder.AppendLine("      <p>Reload the page to generate another deterministic SVG mascot from the GnOuGo.Assets.Bears library.</p>");
    builder.AppendLine("      <dl>");
    builder.Append("        <dt>Seed</dt><dd>").Append(options.Seed).AppendLine("</dd>");
    builder.Append("        <dt>Role</dt><dd>").Append(options.Role).AppendLine("</dd>");
    builder.Append("        <dt>Emotion</dt><dd>").Append(options.Emotion).AppendLine("</dd>");
    builder.Append("        <dt>Accessory</dt><dd>").Append(options.Accessory).AppendLine("</dd>");
    builder.Append("        <dt>State</dt><dd>").Append(options.State).AppendLine("</dd>");
    builder.Append("        <dt>Theme</dt><dd>").Append(options.Theme).AppendLine("</dd>");
    builder.Append("        <dt>Fur</dt><dd>").Append(options.FurPalette).AppendLine("</dd>");
    builder.Append("        <dt>Eyes</dt><dd>").Append(options.EyeStyle).AppendLine("</dd>");
    builder.Append("        <dt>Headphones</dt><dd>").Append(options.HasHeadphones ? "Yes" : "No").AppendLine("</dd>");
    builder.Append("        <dt>Bow tie</dt><dd>").Append(options.HasBowTie ? "Yes" : "No").AppendLine("</dd>");
    builder.AppendLine("      </dl>");
    builder.AppendLine("      <a href=\"/\">Generate another</a>");
    builder.AppendLine("    </section>");
    builder.AppendLine("  </main>");
    builder.AppendLine("</body>");
    builder.AppendLine("</html>");
    return builder.ToString();
}
