# ClientApp - OTLP Tenant Collector UI

Modern React application built with Vite, SCSS, and BEM methodology.

## 🚀 Technologies

- **React 18.3** - UI framework
- **Vite 6** - Fast build tool
- **SCSS** - CSS preprocessor
- **BEM** - CSS naming methodology (Block Element Modifier)

## 📁 Project Structure

```
ClientApp/
├── src/
│   ├── components/          # React components
│   │   ├── App.jsx         # Root component
│   │   ├── Header.jsx      # Header with refresh button
│   │   ├── ConfigPanel.jsx # Tenant/API key configuration
│   │   ├── TraceList.jsx   # Trace list
│   │   ├── TraceListItem.jsx # Trace item
│   │   ├── TraceDetail.jsx # Trace details
│   │   ├── SpanTree.jsx    # Span tree
│   │   └── SpanNode.jsx    # Span node (recursive)
│   │
│   ├── styles/              # SCSS styles (BEM)
│   │   ├── main.scss       # Entry point
│   │   ├── _variables.scss # Global variables
│   │   ├── _base.scss      # Base styles
│   │   └── components/      # Component styles
│   │       ├── _header.scss
│   │       ├── _button.scss
│   │       ├── _panel.scss
│   │       ├── _field.scss
│   │       ├── _badge.scss
│   │       ├── _grid.scss
│   │       ├── _error.scss
│   │       ├── _trace-list.scss
│   │       ├── _trace-item.scss
│   │       ├── _trace-detail.scss
│   │       ├── _span-tree.scss
│   │       ├── _span-node.scss
│   │       └── _attributes.scss
│   │
│   └── main.jsx            # React entry point
│
├── scripts/
│   └── copy-to-wwwroot.mjs # Script to copy to wwwroot
│
├── index.html              # HTML template
├── package.json            # npm dependencies
└── vite.config.js          # Vite configuration
```

## 🎨 BEM Methodology

### Principle
BEM (Block Element Modifier) structures CSS in a modular and maintainable way.

### Structure
```scss
.block { }              // Main component
.block__element { }     // Part of the block
.block--modifier { }    // Block variation
.block__element--modifier { } // Element variation
```

### Examples in this project

#### Block: `.trace-item`
```scss
.trace-item {
  // Main block styles
  display: flex;
  padding: 10px;
  
  // Element: __content
  &__content {
    flex: 1;
  }
  
  // Element: __id
  &__id {
    font-family: monospace;
  }
  
  // Modifier: --selected
  &--selected {
    border-color: green;
  }
}
```

Usage in React:
```jsx
<div className="trace-item trace-item--selected">
  <div className="trace-item__content">
    <div className="trace-item__id">abc123</div>
  </div>
</div>
```

### Advantages of BEM

✅ **No CSS conflicts** - Unique and descriptive names  
✅ **Easy to maintain** - Clear structure  
✅ **Reusable** - Independent components  
✅ **Self-documenting** - The name explains the purpose  

## 🛠️ Installation

```bash
# With npm
npm install

# With pnpm (recommended)
pnpm install

# With yarn
yarn install
```

## 🔨 Commands

### Development
```bash
# Start the development server (with HMR)
npm run dev
# or
pnpm dev

# The application will be available at http://localhost:5173
```

### Production build
```bash
# Build and copy to wwwroot/
npm run build
# or
pnpm build

# Files are generated in:
# - dist/ (temporary build)
# - ../src/OtlpTenantCollector/wwwroot/assets/ (final copy)
```

### Preview
```bash
# Preview the production build
npm run preview
# or
pnpm preview
```

## 📦 Build Output

The build generates:
- `assets/app.mjs` - Compiled JavaScript (bundled React)
- `assets/styles.css` - CSS compiled from SCSS
- `index.html` - HTML with asset references

These files are automatically copied to `../src/OtlpTenantCollector/wwwroot/` by the `copy-to-wwwroot.mjs` script.

## 🎯 React Components

### App.jsx
Root component that manages:
- Global state (tenant, API key, traces)
- Loading traces via API
- LocalStorage for persistence

### Header.jsx
- Application title
- Refresh button with loading state

### ConfigPanel.jsx
- Configuration fields (Tenant ID, API Key, Limit)
- Error display

### TraceList.jsx + TraceListItem.jsx
- List of recent traces
- Trace selection
- Badge with span count

### TraceDetail.jsx
- Trace detail display
- States: loading, empty, or content

### SpanTree.jsx + SpanNode.jsx
- Hierarchical span tree
- Expand/collapse nodes
- Attribute display
- Duration and status badges

## 🎨 SCSS Variables

### Colors
```scss
$color-bg-primary: #0b0f19;
$color-bg-secondary: #11182a;
$color-text-primary: #e6e8ee;
$color-border: rgba(255, 255, 255, 0.08);
```

### Spacing
```scss
$spacing-xs: 6px;
$spacing-sm: 10px;
$spacing-md: 16px;
$spacing-lg: 24px;
```

### Typography
```scss
$font-family-base: system-ui, ...;
$font-family-mono: ui-monospace, ...;
$font-size-sm: 12px;
$font-size-base: 14px;
```

## 🌐 API Integration

The application communicates with the .NET API via:

### Endpoints
```
GET /api/tenants/{tenantId}/traces/recent?limit=50
GET /api/tenants/{tenantId}/traces/{traceId}
```

### Headers
```
X-Api-Key: <your-api-key>
```

## 🔧 Vite Configuration

The `vite.config.js` file configures:
- React plugin with Fast Refresh
- Custom output file names
- Optimized build structure

## 📝 Custom Scripts

### copy-to-wwwroot.mjs
Automatic copy of built files to the .NET project's `wwwroot` folder.

```javascript
// Copies dist/* to ../src/OtlpTenantCollector/wwwroot/
```

## 🎓 Best Practices

### 1. Components
- One component per file
- Typed props (or PropTypes)
- Decompose into small reusable components

### 2. SCSS/BEM
- One SCSS file per component
- Strict adherence to BEM convention
- Variables for all reusable values
- No deeply nested selectors (max 2-3 levels)

### 3. State
- useState for local state
- Minimal props drilling
- LocalStorage for persistence

### 4. Performance
- useMemo for expensive computations (e.g., span tree)
- Lazy loading when needed
- Optimize re-renders

## 🚀 Deployment

1. Build the application:
   ```bash
   pnpm build
   ```

2. Files are automatically copied to `wwwroot/`

3. The .NET API serves the static files

4. Access the UI at: `http://localhost:18318/`

## 🐛 Debugging

### Development mode
```bash
pnpm dev
```
- Hot Module Replacement (HMR) active
- Source maps available
- React DevTools compatible

### Logs
```javascript
console.log('Debug:', data);
```

### React DevTools
Browser extension to inspect React components.

## 📚 Resources

- [React Documentation](https://react.dev/)
- [Vite Documentation](https://vitejs.dev/)
- [SCSS Documentation](https://sass-lang.com/)
- [BEM Methodology](https://getbem.com/)

## ✅ Development Checklist

- [x] Modular component structure
- [x] BEM methodology for styles
- [x] Centralized SCSS variables
- [x] Responsive design (adaptive grid)
- [x] State management with hooks
- [x] LocalStorage persistence
- [x] Error handling
- [x] Loading states
- [x] Custom scrollbars
- [x] Subtle animations
- [x] Optimized build

---

**Built with ❤️ using React + SCSS + BEM**
