using System.Globalization;
using System.Net;
using System.Text;
using GnOuGo.Assets.Bears;

internal static class BearTextDemoPage
{
    public static string Render(GnouGnouBearTextOptions options)
    {
        var bear = options.BearOptions;
        var text = options.TextOptions;
        var suggestedMargin = SuggestedMargin(text.Size, text.Animation);
        var starColor = text.StarColor ?? text.GradientColors[^1];
        var builder = new StringBuilder(capacity: 38_000);
        builder.AppendLine("""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="cache-control" content="no-store">
  <title>GnOuGo Bear + Text Playground</title>
  <style>
    :root { color-scheme: light; font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #eaf2fb; color: #17243a; }
    * { box-sizing: border-box; }
    body { margin: 0; min-height: 100vh; background: radial-gradient(circle at 8% 0%, #fff 0, #edf5ff 38%, #e3ebf8 100%); }
    button, input, select { font: inherit; }
    .shell { width: min(1540px, 100%); margin: 0 auto; padding: 26px clamp(14px,3vw,44px) 48px; }
    .topbar { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 12px; align-items: center; margin-bottom: 21px; }
    .nav { display: flex; flex-wrap: wrap; gap: 15px; }
    .nav a { color: #315f9c; font-weight: 800; text-decoration: none; }
    .badge { border: 1px solid #b9d2ee; border-radius: 999px; padding: 7px 11px; background: rgba(255,255,255,.74); color: #41698f; font-size: .73rem; font-weight: 850; letter-spacing: .08em; text-transform: uppercase; }
    header { max-width: 900px; margin-bottom: 26px; }
    .eyebrow { margin: 0 0 7px; color: #2e67b3; font-size: .76rem; font-weight: 900; letter-spacing: .13em; text-transform: uppercase; }
    h1 { margin: 0; color: #173b6d; font-size: clamp(2.3rem,5vw,5rem); line-height: .95; letter-spacing: -.055em; }
    .intro { max-width: 800px; margin: 14px 0 0; color: #52637b; font-size: 1rem; line-height: 1.62; }
    .workspace { display: grid; grid-template-columns: minmax(330px,440px) minmax(0,1fr); gap: 22px; align-items: start; }
    .panel { border: 1px solid rgba(103,132,170,.24); border-radius: 22px; background: rgba(255,255,255,.89); box-shadow: 0 18px 44px rgba(37,66,104,.1); backdrop-filter: blur(10px); }
    .controls { display: grid; gap: 12px; }
    details { border-bottom: 1px solid #dbe5f1; }
    details:last-child { border-bottom: 0; }
    summary { padding: 17px 20px; color: #1b416f; font-size: .92rem; font-weight: 900; cursor: pointer; list-style-position: outside; }
    .fields { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 14px 12px; padding: 0 20px 21px; }
    .span-2 { grid-column: 1 / -1; }
    label, .field { display: grid; gap: 6px; color: #52657e; font-size: .73rem; font-weight: 850; letter-spacing: .035em; text-transform: uppercase; }
    input[type="text"], input[type="number"], select { width: 100%; min-height: 41px; border: 1px solid #b9cbe0; border-radius: 10px; padding: 0 11px; background: #fff; color: #17243a; outline: none; }
    input:focus, select:focus { border-color: #4a86ca; box-shadow: 0 0 0 3px rgba(74,134,202,.14); }
    .range-row { display: grid; grid-template-columns: minmax(0,1fr) 72px; gap: 9px; align-items: center; }
    input[type="range"] { width: 100%; accent-color: #3379bd; }
    output { display: grid; place-items: center; min-height: 34px; border-radius: 9px; background: #eaf2fb; color: #285b91; font-size: .76rem; font-weight: 900; text-transform: none; }
    .checks { display: flex; flex-wrap: wrap; gap: 10px 15px; }
    .check { display: flex; gap: 7px; align-items: center; color: #5d718a; font-size: .77rem; font-weight: 750; text-transform: none; letter-spacing: 0; }
    .check input { width: 17px; height: 17px; accent-color: #3379bd; }
    .color-list { display: grid; gap: 8px; }
    .color-row { display: grid; grid-template-columns: 44px minmax(0,1fr) auto; gap: 8px; align-items: center; }
    input[type="color"] { width: 44px; height: 37px; border: 1px solid #b9cbe0; border-radius: 9px; padding: 4px; background: #fff; cursor: pointer; }
    .color-value { min-width: 0; color: #405873; font: 750 .76rem/1 ui-monospace,SFMono-Regular,Menlo,monospace; }
    .small-button { min-height: 34px; border: 1px solid #c1d1e4; border-radius: 9px; padding: 0 10px; background: #f8fbff; color: #356698; font-size: .72rem; font-weight: 850; cursor: pointer; }
    .small-button:disabled { opacity: .42; cursor: not-allowed; }
    .preview-panel { position: sticky; top: 16px; overflow: hidden; }
    .preview-head, .preview-foot { display: flex; justify-content: space-between; gap: 16px; align-items: center; padding: 15px 18px; }
    .preview-head { border-bottom: 1px solid #dbe5f1; }
    .preview-foot { border-top: 1px solid #dbe5f1; }
    .preview-title { display: flex; align-items: center; gap: 10px; color: #1b416f; font-size: .9rem; font-weight: 900; }
    .live-dot { width: 9px; height: 9px; border-radius: 50%; background: #20b989; box-shadow: 0 0 0 5px rgba(32,185,137,.12); }
    .preview-background { width: auto; min-height: 37px; padding-right: 32px; font-size: .75rem; font-weight: 800; }
    .preview-surface { min-height: 560px; display: grid; place-items: center; overflow: auto; padding: clamp(24px,5vw,70px); }
    .preview-surface[data-background="light"] { background: #fff; }
    .preview-surface[data-background="dark"] { background: #162238; }
    .preview-surface[data-background="checker"] { background-color: #fff; background-image: linear-gradient(45deg,#e4eaf1 25%,transparent 25%),linear-gradient(-45deg,#e4eaf1 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#e4eaf1 75%),linear-gradient(-45deg,transparent 75%,#e4eaf1 75%); background-size: 28px 28px; background-position: 0 0,0 14px,14px -14px,-14px 0; }
    #lockup-preview { display: block; max-width: 100%; max-height: 440px; width: auto; height: auto; }
    #preview-status { min-width: 0; color: #667a92; font-size: .77rem; line-height: 1.4; }
    #preview-status.error { color: #b43e4c; font-weight: 750; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
    .action { min-height: 37px; display: inline-flex; align-items: center; justify-content: center; border: 0; border-radius: 10px; padding: 0 12px; background: #245aa6; color: #fff; font-size: .76rem; font-weight: 850; text-decoration: none; cursor: pointer; }
    .action.secondary { border: 1px solid #c4d3e7; background: #fff; color: #245aa6; }
    @media (max-width: 980px) { .workspace { grid-template-columns: 1fr; } .preview-panel { position: static; } .preview-surface { min-height: 390px; } }
    @media (max-width: 560px) { .shell { padding-inline: 11px; } .fields { grid-template-columns: 1fr; padding-inline: 16px; } .span-2 { grid-column: auto; } .preview-head,.preview-foot { align-items: flex-start; flex-direction: column; } .actions { justify-content: flex-start; } .preview-surface { min-height: 310px; padding: 22px 12px; } }
    @media (prefers-reduced-motion: reduce) { * { transition: none !important; } }
  </style>
</head>
<body>
  <div class="shell">
    <div class="topbar">
      <nav class="nav"><a href="/">← Bear gallery</a><a href="/text">Text playground</a></nav>
      <span class="badge">Combined generator</span>
    </div>
    <header>
      <p class="eyebrow">GnOuGo.Assets.Bears</p>
      <h1>Bear + text playground</h1>
      <p class="intro">Compose the mascot and rounded wordmark in one standalone SVG. Bear and text appearance, sizing, spacing, and animations remain independent.</p>
    </header>
    <div class="workspace">
      <form class="panel controls" id="lockup-controls">
""");
        builder.AppendLine("        <details open><summary>Composition</summary><div class=\"fields\">");
        AppendRange(builder, "Gap", "gap", options.Gap, 0, 512, 1, "px", span: true);
        AppendRange(builder, "Bear size", "bear-size", bear.Size, 64, 1024, 1, "px");
        AppendRange(builder, "Text size", "text-size", text.Size, 16, 1024, 1, "px");
        builder.AppendLine("        </div></details>");

        builder.AppendLine("        <details open><summary>Bear</summary><div class=\"fields\">");
        AppendNumber(builder, "Seed", "seed", bear.Seed);
        AppendEnumSelect(builder, "Animation", "bear-animation", bear.Animation);
        AppendEnumSelect(builder, "Role", "role", bear.Role);
        AppendEnumSelect(builder, "Emotion", "emotion", bear.Emotion);
        AppendEnumSelect(builder, "Accessory", "accessory", bear.Accessory);
        AppendEnumSelect(builder, "State", "state", bear.State);
        AppendEnumSelect(builder, "Theme", "theme", bear.Theme);
        AppendEnumSelect(builder, "Fur palette", "fur", bear.FurPalette);
        AppendEnumSelect(builder, "Eye style", "eyes", bear.EyeStyle);
        AppendEnumSelect(builder, "Nose style", "nose", bear.NoseStyle);
        AppendEnumSelect(builder, "Beard style", "beard-style", bear.BeardStyle);
        AppendRange(builder, "Accessory color", "accessory-color", bear.AccessoryColorVariant, 0, 5, 1, string.Empty);
        builder.AppendLine("          <div class=\"checks span-2\">");
        AppendCheckbox(builder, "Headphones", "headphones", bear.HasHeadphones);
        AppendCheckbox(builder, "Bow tie", "bow-tie", bear.HasBowTie);
        AppendCheckbox(builder, "Beard", "beard", bear.HasBeard);
        builder.AppendLine("          </div>");
        builder.AppendLine("        </div></details>");

        builder.AppendLine("        <details open><summary>Text</summary><div class=\"fields\">");
        builder.Append("<label class=\"span-2\" for=\"text-value\">Content<input id=\"text-value\" type=\"text\" maxlength=\"128\" autocomplete=\"off\" value=\"")
            .Append(Html(text.Text)).AppendLine("\"></label>");
        AppendEnumSelect(builder, "Animation", "text-animation", text.Animation);
        AppendRange(builder, "Sparkle count", "star-count", text.StarCount, 0, 8, 1, string.Empty);
        AppendMargin(builder, "Horizontal margin", "margin-x", text.HorizontalMargin ?? suggestedMargin, text.HorizontalMargin is null);
        AppendMargin(builder, "Vertical margin", "margin-y", text.VerticalMargin ?? suggestedMargin, text.VerticalMargin is null);
        AppendRange(builder, "Sparkle scale", "star-scale", text.StarScale, 0.25, 3, 0.05, "×");
        builder.AppendLine("          <div class=\"field\"><span>Sparkle color</span><div class=\"color-row\">");
        builder.Append("<input id=\"star-color\" type=\"color\" value=\"").Append(Html(starColor))
            .AppendLine("\"><span class=\"color-value\" id=\"star-color-value\"></span></div>");
        builder.Append("<label class=\"check\" for=\"star-color-auto\"><input id=\"star-color-auto\" type=\"checkbox\"");
        if (text.StarColor is null)
            builder.Append(" checked");
        builder.AppendLine(">Follow gradient end</label></div>");
        builder.AppendLine("          <div class=\"field span-2\"><span>Gradient colors</span><div class=\"color-list\" id=\"gradient-colors\">");
        foreach (var color in text.GradientColors)
            AppendColorRow(builder, color);
        builder.AppendLine("          </div><button class=\"small-button\" id=\"add-color\" type=\"button\">Add gradient stop</button></div>");
        builder.AppendLine("        </div></details>");

        builder.AppendLine("""
      </form>
      <section class="panel preview-panel" aria-label="Combined SVG preview">
        <div class="preview-head">
          <div class="preview-title"><span class="live-dot" aria-hidden="true"></span>Live combined SVG</div>
          <select class="preview-background" id="preview-background" aria-label="Preview background">
            <option value="light">Light background</option>
            <option value="checker">Transparency grid</option>
            <option value="dark">Dark background</option>
          </select>
        </div>
        <div class="preview-surface" id="preview-surface" data-background="light">
          <img id="lockup-preview" alt="Generated GnOuGo bear and text SVG preview">
        </div>
        <div class="preview-foot">
          <span id="preview-status" role="status" aria-live="polite">Preparing preview…</span>
          <div class="actions">
            <a class="action secondary" href="/bear-text">Reset</a>
            <button class="action secondary" id="copy-link" type="button">Copy SVG URL</button>
            <a class="action secondary" id="open-link" target="_blank" rel="noopener">Open SVG</a>
            <a class="action" id="download-link" download="gnougnou-bear-text.svg">Download</a>
          </div>
        </div>
      </section>
    </div>
  </div>
  <script>
    (() => {
      const form = document.querySelector('#lockup-controls');
      const preview = document.querySelector('#lockup-preview');
      const status = document.querySelector('#preview-status');
      const openLink = document.querySelector('#open-link');
      const downloadLink = document.querySelector('#download-link');
      const copyLink = document.querySelector('#copy-link');
      const colors = document.querySelector('#gradient-colors');
      const addColor = document.querySelector('#add-color');
      const background = document.querySelector('#preview-background');
      const surface = document.querySelector('#preview-surface');
      const starColor = document.querySelector('#star-color');
      const starColorValue = document.querySelector('#star-color-value');
      const starColorAuto = document.querySelector('#star-color-auto');
      const marginX = document.querySelector('#margin-x');
      const marginY = document.querySelector('#margin-y');
      const marginXAuto = document.querySelector('#margin-x-auto');
      const marginYAuto = document.querySelector('#margin-y-auto');
      let timer;
      let request;
      let objectUrl;

      const byId = id => document.querySelector('#' + id);
      const colorInputs = () => [...colors.querySelectorAll('input[type="color"]')];
      const checked = id => byId(id).checked.toString();

      function suggestedMargin() {
        const factors = { None: .14, Idle: .22, Wave: .2, Bounce: .24 };
        return Math.round(Number(byId('text-size').value) * factors[byId('text-animation').value]);
      }

      function updateOutputs() {
        ['gap','bear-size','text-size','accessory-color','star-count','star-scale','margin-x','margin-y'].forEach(id => {
          const input = byId(id);
          const output = byId(id + '-output');
          if (output)
            output.value = input.value + (input.dataset.unit || '');
        });
        if (marginXAuto.checked)
          marginX.value = suggestedMargin();
        if (marginYAuto.checked)
          marginY.value = suggestedMargin();
        marginX.disabled = marginXAuto.checked;
        marginY.disabled = marginYAuto.checked;
        byId('margin-x-output').value = marginXAuto.checked ? 'Auto ' + marginX.value : marginX.value + 'px';
        byId('margin-y-output').value = marginYAuto.checked ? 'Auto ' + marginY.value : marginY.value + 'px';
        const inputs = colorInputs();
        colors.querySelectorAll('.remove-color').forEach(button => button.disabled = inputs.length <= 2);
        addColor.disabled = inputs.length >= 8;
        colors.querySelectorAll('.color-row').forEach(row => {
          row.querySelector('.color-value').textContent = row.querySelector('input').value.toUpperCase();
        });
        if (starColorAuto.checked)
          starColor.value = inputs.at(-1).value;
        starColor.disabled = starColorAuto.checked;
        starColorValue.textContent = starColor.value.toUpperCase();
        surface.dataset.background = background.value;
      }

      function parameters() {
        const query = new URLSearchParams();
        query.set('gap', byId('gap').value);
        query.set('seed', byId('seed').value);
        query.set('bearSize', byId('bear-size').value);
        query.set('bearAnimation', byId('bear-animation').value);
        query.set('role', byId('role').value);
        query.set('emotion', byId('emotion').value);
        query.set('accessory', byId('accessory').value);
        query.set('accessoryColor', byId('accessory-color').value);
        query.set('state', byId('state').value);
        query.set('theme', byId('theme').value);
        query.set('fur', byId('fur').value);
        query.set('eyes', byId('eyes').value);
        query.set('nose', byId('nose').value);
        query.set('beardStyle', byId('beard-style').value);
        query.set('headphones', checked('headphones'));
        query.set('bowTie', checked('bow-tie'));
        query.set('beard', checked('beard'));
        query.set('text', byId('text-value').value);
        query.set('textSize', byId('text-size').value);
        query.set('textAnimation', byId('text-animation').value);
        if (!marginXAuto.checked)
          query.set('marginX', marginX.value);
        if (!marginYAuto.checked)
          query.set('marginY', marginY.value);
        colorInputs().forEach(input => query.append('color', input.value));
        query.set('stars', byId('star-count').value);
        query.set('starScale', byId('star-scale').value);
        if (!starColorAuto.checked)
          query.set('starColor', starColor.value);
        query.set('idPrefix', 'bear-text-demo');
        return query;
      }

      async function renderPreview() {
        updateOutputs();
        const query = parameters();
        const endpoint = '/bear-text.svg?' + query.toString();
        openLink.href = endpoint;
        downloadLink.href = endpoint;
        history.replaceState(null, '', '/bear-text?' + query.toString());
        status.classList.remove('error');
        status.textContent = 'Rendering combined SVG…';
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
          status.textContent = root.getAttribute('width') + ' × ' + root.getAttribute('height') + ' SVG · bear ' + byId('bear-animation').value + ' · text ' + byId('text-animation').value;
        } catch (error) {
          if (error.name === 'AbortError')
            return;
          status.classList.add('error');
          status.textContent = error.message;
          preview.removeAttribute('src');
        }
      }

      function schedule() {
        clearTimeout(timer);
        timer = setTimeout(renderPreview, 110);
      }

      function createColorRow(value) {
        const row = document.createElement('div');
        row.className = 'color-row';
        const input = document.createElement('input');
        input.type = 'color';
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

      form.addEventListener('input', () => { updateOutputs(); schedule(); });
      form.addEventListener('change', schedule);
      form.addEventListener('submit', event => { event.preventDefault(); renderPreview(); });
      background.addEventListener('change', updateOutputs);
      addColor.addEventListener('click', () => {
        const inputs = colorInputs();
        if (inputs.length >= 8)
          return;
        colors.append(createColorRow(inputs.at(-1).value));
        updateOutputs();
        schedule();
      });
      colors.addEventListener('click', event => {
        const button = event.target.closest('.remove-color');
        if (!button || colorInputs().length <= 2)
          return;
        button.closest('.color-row').remove();
        updateOutputs();
        schedule();
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

    private static void AppendRange(
        StringBuilder builder,
        string label,
        string id,
        double value,
        double min,
        double max,
        double step,
        string unit,
        bool span = false)
    {
        builder.Append("<label").Append(span ? " class=\"span-2\"" : string.Empty).Append(" for=\"").Append(id).Append("\">")
            .Append(label).AppendLine("<span class=\"range-row\">");
        builder.Append("<input id=\"").Append(id).Append("\" type=\"range\" min=\"").Append(Number(min))
            .Append("\" max=\"").Append(Number(max)).Append("\" step=\"").Append(Number(step))
            .Append("\" value=\"").Append(Number(value)).Append("\" data-unit=\"").Append(Html(unit)).AppendLine("\">");
        builder.Append("<output id=\"").Append(id).Append("-output\" for=\"").Append(id).AppendLine("\"></output></span></label>");
    }

    private static void AppendNumber(StringBuilder builder, string label, string id, int value)
    {
        builder.Append("<label for=\"").Append(id).Append("\">").Append(label)
            .Append("<input id=\"").Append(id).Append("\" type=\"number\" value=\"").Append(value)
            .AppendLine("\"></label>");
    }

    private static void AppendEnumSelect<T>(StringBuilder builder, string label, string id, T selected)
        where T : struct, Enum
    {
        builder.Append("<label for=\"").Append(id).Append("\">").Append(label)
            .Append("<select id=\"").Append(id).AppendLine("\">");
        foreach (var value in Enum.GetValues<T>())
        {
            builder.Append("<option value=\"").Append(value).Append('"');
            if (EqualityComparer<T>.Default.Equals(value, selected))
                builder.Append(" selected");
            builder.Append('>').Append(value).AppendLine("</option>");
        }
        builder.AppendLine("</select></label>");
    }

    private static void AppendCheckbox(StringBuilder builder, string label, string id, bool isChecked)
    {
        builder.Append("<label class=\"check\" for=\"").Append(id).Append("\"><input id=\"").Append(id)
            .Append("\" type=\"checkbox\"");
        if (isChecked)
            builder.Append(" checked");
        builder.Append('>').Append(label).AppendLine("</label>");
    }

    private static void AppendMargin(
        StringBuilder builder,
        string label,
        string id,
        double value,
        bool automatic)
    {
        builder.Append("<div class=\"field\"><label for=\"").Append(id).Append("\">").Append(label)
            .AppendLine("<span class=\"range-row\">");
        builder.Append("<input id=\"").Append(id).Append("\" type=\"range\" min=\"0\" max=\"4096\" step=\"1\" value=\"")
            .Append(Number(value)).AppendLine("\">");
        builder.Append("<output id=\"").Append(id).Append("-output\" for=\"").Append(id).AppendLine("\"></output></span></label>");
        builder.Append("<label class=\"check\" for=\"").Append(id).Append("-auto\"><input id=\"").Append(id)
            .Append("-auto\" type=\"checkbox\"");
        if (automatic)
            builder.Append(" checked");
        builder.AppendLine(">Automatic safe margin</label></div>");
    }

    private static void AppendColorRow(StringBuilder builder, string color)
    {
        builder.Append("<div class=\"color-row\"><input type=\"color\" aria-label=\"Gradient color\" value=\"")
            .Append(Html(color)).Append("\"><span class=\"color-value\">")
            .Append(Html(color.ToUpperInvariant()))
            .AppendLine("</span><button class=\"small-button remove-color\" type=\"button\">Remove</button></div>");
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
