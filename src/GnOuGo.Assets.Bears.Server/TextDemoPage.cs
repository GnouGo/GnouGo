using System.Globalization;
using System.Net;
using System.Text;
using GnOuGo.Assets.Bears;

internal static class TextDemoPage
{
    public static string Render(GnouGnouTextOptions options)
    {
        var starColor = options.StarColor ?? options.GradientColors[^1];
        var suggestedMargin = SuggestedMargin(options.Size, options.Animation);
        var horizontalMargin = options.HorizontalMargin ?? suggestedMargin;
        var verticalMargin = options.VerticalMargin ?? suggestedMargin;
        var builder = new StringBuilder(capacity: 32_000);
        builder.Append("""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="cache-control" content="no-store">
  <title>GnOuGo Text SVG Playground</title>
  <style>
    :root { color-scheme: light; font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #eaf2fb; color: #17243a; }
    * { box-sizing: border-box; }
    body { margin: 0; min-height: 100vh; background: radial-gradient(circle at 8% 0%, #fff 0, #edf5ff 38%, #e3ebf8 100%); }
    button, input, select { font: inherit; }
    button, a { -webkit-tap-highlight-color: transparent; }
    .shell { width: min(1480px, 100%); margin: 0 auto; padding: 28px clamp(16px, 3vw, 46px) 46px; }
    .topbar { display: flex; justify-content: space-between; gap: 20px; align-items: center; margin-bottom: 22px; }
    .topbar-nav { display: flex; flex-wrap: wrap; gap: 15px; }
    .back { color: #315f9c; font-weight: 800; text-decoration: none; }
    .badge { border: 1px solid #b9d2ee; border-radius: 999px; padding: 7px 11px; background: rgba(255,255,255,.72); color: #41698f; font-size: .74rem; font-weight: 850; letter-spacing: .08em; text-transform: uppercase; }
    header { max-width: 860px; margin-bottom: 28px; }
    .eyebrow { margin: 0 0 7px; color: #2e67b3; font-size: .76rem; font-weight: 900; letter-spacing: .13em; text-transform: uppercase; }
    h1 { margin: 0; color: #173b6d; font-size: clamp(2.35rem, 5vw, 5.2rem); line-height: .94; letter-spacing: -.055em; }
    .intro { max-width: 760px; margin: 15px 0 0; color: #52637b; font-size: 1.01rem; line-height: 1.62; }
    .workspace { display: grid; grid-template-columns: minmax(300px, 390px) minmax(0, 1fr); gap: 22px; align-items: start; }
    .panel { border: 1px solid rgba(103,132,170,.24); border-radius: 22px; background: rgba(255,255,255,.88); box-shadow: 0 18px 44px rgba(37,66,104,.1); backdrop-filter: blur(10px); }
    .controls { padding: 21px; }
    .section + .section { margin-top: 22px; padding-top: 20px; border-top: 1px solid #dbe5f1; }
    .section-head { display: flex; justify-content: space-between; gap: 12px; align-items: center; margin-bottom: 13px; }
    h2 { margin: 0; color: #1b416f; font-size: .92rem; letter-spacing: .015em; }
    .section-count { color: #71839a; font-size: .73rem; font-weight: 800; }
    label, .field { display: grid; gap: 7px; color: #52657e; font-size: .76rem; font-weight: 850; letter-spacing: .035em; text-transform: uppercase; }
    label + label, .field + .field { margin-top: 14px; }
    input[type="text"], input[type="number"], select { width: 100%; min-height: 43px; border: 1px solid #b9cbe0; border-radius: 11px; padding: 0 12px; background: #fff; color: #17243a; outline: none; transition: border-color .16s ease, box-shadow .16s ease; }
    input:focus, select:focus { border-color: #4a86ca; box-shadow: 0 0 0 3px rgba(74,134,202,.14); }
    .range-row { display: grid; grid-template-columns: minmax(0, 1fr) 68px; gap: 10px; align-items: center; }
    input[type="range"] { width: 100%; accent-color: #3379bd; }
    output { display: grid; place-items: center; min-height: 36px; border-radius: 9px; background: #eaf2fb; color: #285b91; font-size: .8rem; font-weight: 900; text-transform: none; }
    .color-list { display: grid; gap: 9px; }
    .color-row { display: grid; grid-template-columns: 45px minmax(0, 1fr) auto; gap: 9px; align-items: center; }
    input[type="color"] { width: 45px; height: 39px; border: 1px solid #b9cbe0; border-radius: 10px; padding: 4px; background: #fff; cursor: pointer; }
    .color-value { min-width: 0; color: #405873; font: 750 .78rem/1 ui-monospace, SFMono-Regular, Menlo, monospace; }
    .small-button { min-height: 35px; border: 1px solid #c1d1e4; border-radius: 9px; padding: 0 10px; background: #f8fbff; color: #356698; font-size: .74rem; font-weight: 850; cursor: pointer; }
    .small-button:hover { background: #ebf3fc; }
    .small-button:disabled { opacity: .42; cursor: not-allowed; }
    .inline-check { display: flex; gap: 9px; align-items: center; margin-top: 10px; color: #5d718a; font-size: .78rem; font-weight: 750; text-transform: none; letter-spacing: 0; }
    .inline-check input { width: 17px; height: 17px; accent-color: #3379bd; }
    .preview-panel { position: sticky; top: 18px; overflow: hidden; }
    .preview-head { display: flex; justify-content: space-between; gap: 18px; align-items: center; padding: 16px 18px; border-bottom: 1px solid #dbe5f1; }
    .preview-title { display: flex; align-items: center; gap: 10px; }
    .live-dot { width: 9px; height: 9px; border-radius: 50%; background: #20b989; box-shadow: 0 0 0 5px rgba(32,185,137,.12); }
    .preview-background { width: auto; min-height: 37px; padding-right: 34px; font-size: .76rem; font-weight: 800; }
    .preview-surface { min-height: 500px; display: grid; place-items: center; overflow: auto; padding: clamp(28px, 6vw, 80px); transition: background .18s ease; }
    .preview-surface[data-background="light"] { background: #fff; }
    .preview-surface[data-background="dark"] { background: #162238; }
    .preview-surface[data-background="checker"] { background-color: #fff; background-image: linear-gradient(45deg,#e4eaf1 25%,transparent 25%),linear-gradient(-45deg,#e4eaf1 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#e4eaf1 75%),linear-gradient(-45deg,transparent 75%,#e4eaf1 75%); background-size: 28px 28px; background-position: 0 0,0 14px,14px -14px,-14px 0; }
    #text-preview { display: block; max-width: 100%; max-height: 390px; width: auto; height: auto; }
    .preview-foot { display: flex; justify-content: space-between; gap: 18px; align-items: center; padding: 15px 18px; border-top: 1px solid #dbe5f1; }
    #preview-status { min-width: 0; color: #667a92; font-size: .78rem; line-height: 1.45; }
    #preview-status.error { color: #b43e4c; font-weight: 750; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
    .action { min-height: 38px; display: inline-flex; align-items: center; justify-content: center; border: 0; border-radius: 10px; padding: 0 13px; background: #245aa6; color: #fff; font-size: .78rem; font-weight: 850; text-decoration: none; cursor: pointer; }
    .action.secondary { border: 1px solid #c4d3e7; background: #fff; color: #245aa6; }
    noscript { display: block; padding: 16px; color: #a43d49; font-weight: 750; }
    @media (max-width: 920px) { .workspace { grid-template-columns: 1fr; } .preview-panel { position: static; } .preview-surface { min-height: 390px; } }
    @media (max-width: 560px) { .shell { padding-inline: 12px; } .controls { padding: 17px; } .preview-head, .preview-foot { align-items: flex-start; flex-direction: column; } .actions { justify-content: flex-start; } .preview-surface { min-height: 320px; padding: 25px 16px; } }
    @media (prefers-reduced-motion: reduce) { * { scroll-behavior: auto !important; transition: none !important; } }
  </style>
</head>
<body>
  <div class="shell">
    <div class="topbar">
      <nav class="topbar-nav"><a class="back" href="/">← Bear gallery</a><a class="back" href="/bear-text">Bear + text</a></nav>
      <span class="badge">Live generator</span>
    </div>
    <header>
      <p class="eyebrow">GnOuGo.Assets.Bears</p>
      <h1>Text SVG playground</h1>
      <p class="intro">Tune the rounded wordmark, gradient, sparkles, and motion. Every change is rendered by the same public C# generator shipped in the NuGet package.</p>
    </header>
    <div class="workspace">
      <form class="panel controls" id="text-controls">
        <section class="section">
          <div class="section-head"><h2>Text</h2></div>
          <label for="text-value">Content
            <input id="text-value" name="text" type="text" maxlength="128" autocomplete="off" value="
""");
        builder.Append(Html(options.Text));
        builder.Append("""
">
          </label>
          <label for="text-size">Nominal size
            <span class="range-row">
              <input id="text-size" name="size" type="range" min="16" max="1024" step="1" value="
""");
        builder.Append(options.Size);
        builder.AppendLine("""
">
              <output id="size-output" for="text-size"></output>
            </span>
          </label>
          <label for="animation">Animation
            <select id="animation" name="animation">
""");
        AppendOption(builder, "None", options.Animation == GnouGnouTextAnimation.None);
        AppendOption(builder, "Idle", options.Animation == GnouGnouTextAnimation.Idle);
        AppendOption(builder, "Wave", options.Animation == GnouGnouTextAnimation.Wave);
        AppendOption(builder, "Bounce", options.Animation == GnouGnouTextAnimation.Bounce);
        builder.AppendLine("""
            </select>
          </label>
        </section>
""");
        AppendMarginsSection(builder, horizontalMargin, verticalMargin, options);
        builder.AppendLine("""
        <section class="section">
          <div class="section-head">
            <h2>Gradient colors</h2>
            <span class="section-count" id="color-count"></span>
          </div>
          <div class="color-list" id="gradient-colors">
""");
        foreach (var color in options.GradientColors)
            AppendColorRow(builder, color);
        builder.Append("""
          </div>
          <button class="small-button" id="add-color" type="button">Add gradient stop</button>
        </section>
        <section class="section">
          <div class="section-head"><h2>Sparkles</h2></div>
          <label for="star-count">Count
            <span class="range-row">
              <input id="star-count" name="stars" type="range" min="0" max="8" step="1" value="
""");
        builder.Append(options.StarCount);
        builder.Append("""
">
              <output id="star-count-output" for="star-count"></output>
            </span>
          </label>
          <label for="star-scale">Scale
            <span class="range-row">
              <input id="star-scale" name="starScale" type="range" min="0.25" max="3" step="0.05" value="
""");
        builder.Append(Number(options.StarScale));
        builder.Append("""
">
              <output id="star-scale-output" for="star-scale"></output>
            </span>
          </label>
          <div class="field">
            <span>Color</span>
            <div class="color-row">
              <input id="star-color" name="starColor" type="color" value="
""");
        builder.Append(Html(starColor));
        builder.Append("""
">
              <span class="color-value" id="star-color-value"></span>
            </div>
            <label class="inline-check" for="star-color-auto">
              <input id="star-color-auto" type="checkbox"
""");
        if (options.StarColor is null)
            builder.Append(" checked");
        builder.AppendLine("""
>
              Follow the final gradient color
            </label>
          </div>
        </section>
      </form>
      <section class="panel preview-panel" aria-labelledby="preview-title">
        <div class="preview-head">
          <div class="preview-title"><span class="live-dot" aria-hidden="true"></span><h2 id="preview-title">Live SVG preview</h2></div>
          <select class="preview-background" id="preview-background" aria-label="Preview background">
            <option value="light">Light background</option>
            <option value="checker">Transparency grid</option>
            <option value="dark">Dark background</option>
          </select>
        </div>
        <div class="preview-surface" id="preview-surface" data-background="light">
          <img id="text-preview" alt="Generated gradient text SVG preview">
        </div>
        <noscript>JavaScript is required for the live playground. The <code>/text.svg</code> endpoint remains available directly.</noscript>
        <div class="preview-foot">
          <span id="preview-status" role="status" aria-live="polite">Preparing preview…</span>
          <div class="actions">
            <a class="action secondary" href="/text">Reset</a>
            <button class="action secondary" id="copy-link" type="button">Copy SVG URL</button>
            <a class="action secondary" id="open-link" target="_blank" rel="noopener">Open SVG</a>
            <a class="action" id="download-link" download="gnougnou-text.svg">Download</a>
          </div>
        </div>
      </section>
    </div>
  </div>
  <script>
    (() => {
      const form = document.querySelector('#text-controls');
      const text = document.querySelector('#text-value');
      const size = document.querySelector('#text-size');
      const sizeOutput = document.querySelector('#size-output');
      const animation = document.querySelector('#animation');
      const marginX = document.querySelector('#margin-x');
      const marginXOutput = document.querySelector('#margin-x-output');
      const marginXAuto = document.querySelector('#margin-x-auto');
      const marginY = document.querySelector('#margin-y');
      const marginYOutput = document.querySelector('#margin-y-output');
      const marginYAuto = document.querySelector('#margin-y-auto');
      const colors = document.querySelector('#gradient-colors');
      const colorCount = document.querySelector('#color-count');
      const addColor = document.querySelector('#add-color');
      const starCount = document.querySelector('#star-count');
      const starCountOutput = document.querySelector('#star-count-output');
      const starScale = document.querySelector('#star-scale');
      const starScaleOutput = document.querySelector('#star-scale-output');
      const starColor = document.querySelector('#star-color');
      const starColorValue = document.querySelector('#star-color-value');
      const starColorAuto = document.querySelector('#star-color-auto');
      const background = document.querySelector('#preview-background');
      const surface = document.querySelector('#preview-surface');
      const preview = document.querySelector('#text-preview');
      const status = document.querySelector('#preview-status');
      const openLink = document.querySelector('#open-link');
      const downloadLink = document.querySelector('#download-link');
      const copyLink = document.querySelector('#copy-link');
      let timer;
      let request;
      let objectUrl;

      const colorInputs = () => [...colors.querySelectorAll('input[type="color"]')];

      function suggestedMargin() {
        const factors = { None: .14, Idle: .22, Wave: .2, Bounce: .24 };
        return Math.round(Number(size.value) * factors[animation.value]);
      }

      function refreshColorRows() {
        const inputs = colorInputs();
        colorCount.textContent = inputs.length + ' / 8';
        addColor.disabled = inputs.length >= 8;
        colors.querySelectorAll('.remove-color').forEach(button => button.disabled = inputs.length <= 2);
        colors.querySelectorAll('.color-row').forEach(row => {
          row.querySelector('.color-value').textContent = row.querySelector('input').value.toUpperCase();
        });
        if (starColorAuto.checked) {
          starColor.value = inputs.at(-1).value;
          starColorValue.textContent = starColor.value.toUpperCase();
        }
      }

      function updateOutputs() {
        sizeOutput.value = size.value + 'px';
        if (marginXAuto.checked)
          marginX.value = suggestedMargin();
        if (marginYAuto.checked)
          marginY.value = suggestedMargin();
        marginX.disabled = marginXAuto.checked;
        marginY.disabled = marginYAuto.checked;
        marginXOutput.value = marginXAuto.checked ? 'Auto ' + marginX.value : marginX.value + 'px';
        marginYOutput.value = marginYAuto.checked ? 'Auto ' + marginY.value : marginY.value + 'px';
        starCountOutput.value = starCount.value;
        starScaleOutput.value = Number(starScale.value).toFixed(2) + '×';
        starColor.disabled = starColorAuto.checked;
        starColorValue.textContent = starColor.value.toUpperCase();
        surface.dataset.background = background.value;
        refreshColorRows();
      }

      function createColorRow(value) {
        const row = document.createElement('div');
        row.className = 'color-row';
        const input = document.createElement('input');
        input.type = 'color';
        input.name = 'color';
        input.value = value;
        input.setAttribute('aria-label', 'Gradient color');
        const token = document.createElement('span');
        token.className = 'color-value';
        const remove = document.createElement('button');
        remove.className = 'small-button remove-color';
        remove.type = 'button';
        remove.textContent = 'Remove';
        row.append(input, token, remove);
        return row;
      }

      function buildParameters() {
        const parameters = new URLSearchParams();
        parameters.set('text', text.value);
        parameters.set('size', size.value);
        if (!marginXAuto.checked)
          parameters.set('marginX', marginX.value);
        if (!marginYAuto.checked)
          parameters.set('marginY', marginY.value);
        colorInputs().forEach(input => parameters.append('color', input.value));
        parameters.set('stars', starCount.value);
        parameters.set('starScale', starScale.value);
        parameters.set('animation', animation.value);
        parameters.set('idPrefix', 'text-demo');
        if (!starColorAuto.checked)
          parameters.set('starColor', starColor.value);
        return parameters;
      }

      async function renderPreview() {
        updateOutputs();
        const parameters = buildParameters();
        const endpoint = '/text.svg?' + parameters.toString();
        openLink.href = endpoint;
        downloadLink.href = endpoint;
        history.replaceState(null, '', '/text?' + parameters.toString());
        status.classList.remove('error');
        status.textContent = 'Rendering SVG…';
        request?.abort();
        request = new AbortController();

        try {
          const response = await fetch(endpoint, { signal: request.signal, cache: 'no-store' });
          const body = await response.text();
          if (!response.ok)
            throw new Error(body || 'HTTP ' + response.status);

          if (objectUrl)
            URL.revokeObjectURL(objectUrl);
          objectUrl = URL.createObjectURL(new Blob([body], { type: 'image/svg+xml' }));
          preview.src = objectUrl;
          const root = new DOMParser().parseFromString(body, 'image/svg+xml').documentElement;
          status.textContent = root.getAttribute('width') + ' × ' + root.getAttribute('height') + ' SVG · ' + colorInputs().length + ' gradient colors';
        } catch (error) {
          if (error.name === 'AbortError')
            return;
          status.classList.add('error');
          status.textContent = error.message;
          preview.removeAttribute('src');
        }
      }

      function schedulePreview() {
        clearTimeout(timer);
        timer = setTimeout(renderPreview, 110);
      }

      form.addEventListener('input', event => {
        if (event.target.matches('#gradient-colors input[type="color"]') && starColorAuto.checked)
          starColor.value = colorInputs().at(-1).value;
        updateOutputs();
        schedulePreview();
      });

      form.addEventListener('change', schedulePreview);
      form.addEventListener('submit', event => {
        event.preventDefault();
        renderPreview();
      });
      background.addEventListener('change', updateOutputs);
      addColor.addEventListener('click', () => {
        const inputs = colorInputs();
        if (inputs.length >= 8)
          return;
        colors.append(createColorRow(inputs.at(-1).value));
        updateOutputs();
        schedulePreview();
      });
      colors.addEventListener('click', event => {
        const button = event.target.closest('.remove-color');
        if (!button || colorInputs().length <= 2)
          return;
        button.closest('.color-row').remove();
        updateOutputs();
        schedulePreview();
      });
      starColorAuto.addEventListener('change', () => {
        updateOutputs();
        schedulePreview();
      });
      copyLink.addEventListener('click', async () => {
        try {
          await navigator.clipboard.writeText(new URL(openLink.getAttribute('href'), location.origin).href);
          copyLink.textContent = 'Copied';
          setTimeout(() => copyLink.textContent = 'Copy SVG URL', 1400);
        } catch {
          status.classList.add('error');
          status.textContent = 'The browser could not copy the URL.';
        }
      });
      window.addEventListener('beforeunload', () => {
        request?.abort();
        if (objectUrl)
          URL.revokeObjectURL(objectUrl);
      });

      updateOutputs();
      renderPreview();
    })();
  </script>
</body>
</html>
""");
        return builder.ToString();
    }

    private static void AppendColorRow(StringBuilder builder, string color)
    {
        builder.Append("<div class=\"color-row\"><input name=\"color\" type=\"color\" aria-label=\"Gradient color\" value=\"")
            .Append(Html(color)).Append("\"><span class=\"color-value\">")
            .Append(Html(color.ToUpperInvariant()))
            .AppendLine("</span><button class=\"small-button remove-color\" type=\"button\">Remove</button></div>");
    }

    private static void AppendMarginsSection(
        StringBuilder builder,
        double horizontalMargin,
        double verticalMargin,
        GnouGnouTextOptions options)
    {
        builder.AppendLine("        <section class=\"section\">");
        builder.AppendLine("          <div class=\"section-head\"><h2>Canvas margins</h2><span class=\"section-count\">Each side</span></div>");
        AppendMarginControl(
            builder,
            "Horizontal",
            "margin-x",
            horizontalMargin,
            options.HorizontalMargin is null);
        AppendMarginControl(
            builder,
            "Vertical",
            "margin-y",
            verticalMargin,
            options.VerticalMargin is null);
        builder.AppendLine("        </section>");
    }

    private static void AppendMarginControl(
        StringBuilder builder,
        string label,
        string id,
        double value,
        bool automatic)
    {
        builder.Append("          <label for=\"").Append(id).Append("\">").Append(label).AppendLine();
        builder.AppendLine("            <span class=\"range-row\">");
        builder.Append("              <input id=\"").Append(id).Append("\" type=\"range\" min=\"0\" max=\"4096\" step=\"1\" value=\"")
            .Append(Number(value)).AppendLine("\">");
        builder.Append("              <output id=\"").Append(id).Append("-output\" for=\"").Append(id).AppendLine("\"></output>");
        builder.AppendLine("            </span>");
        builder.AppendLine("          </label>");
        builder.Append("          <label class=\"inline-check\" for=\"").Append(id).Append("-auto\"><input id=\"")
            .Append(id).Append("-auto\" type=\"checkbox\"");
        if (automatic)
            builder.Append(" checked");
        builder.AppendLine(">Automatic animation-safe margin</label>");
    }

    private static void AppendOption(StringBuilder builder, string value, bool selected)
    {
        builder.Append("<option value=\"").Append(value).Append('"');
        if (selected)
            builder.Append(" selected");
        builder.Append('>').Append(value).AppendLine("</option>");
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Number(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double SuggestedMargin(int size, GnouGnouTextAnimation animation) =>
        size * (animation switch
        {
            GnouGnouTextAnimation.None => 0.14d,
            GnouGnouTextAnimation.Idle => 0.22d,
            GnouGnouTextAnimation.Wave => 0.2d,
            GnouGnouTextAnimation.Bounce => 0.24d,
            _ => 0.22d
        });
}
