# GnOuGo.Agent (Blazor + Minimal API, Native AOT-ready)

This solution contains:
- **GnOuGo.Agent.Server**: Blazor (server interactive) UI + Minimal API streaming endpoint
- **GnOuGo.Agent.Shared**: shared DTOs

## Frontend (SCSS + offline JS via Vite)

The UI styles and client helpers are bundled with **Vite** into `wwwroot/assets/app.css` and `wwwroot/assets/app.js`.

### Build frontend once

```bash
cd src/GnOuGo.Agent.Server/ClientApp
npm install
npm run build
```

### Dev (optional)

```bash
cd src/GnOuGo.Agent.Server/ClientApp
npm install
npm run dev
```

> Note: for simplicity, this repo ships with pre-built assets in `wwwroot/assets`.

## Model catalog cache

The server uses `IMemoryCache` to cache provider model listings for a short duration.

- Service: `ILLMModelCatalog`
- Default absolute expiration: `30` seconds
- Configuration section: `ModelCatalogCache`

Example:

```json
{
  "ModelCatalogCache": {
	"Enabled": true,
	"AbsoluteExpirationSeconds": 30
  }
}
```

The cache key includes the active provider configuration fingerprint, so changing a provider URL or credentials invalidates the cached entry automatically.

