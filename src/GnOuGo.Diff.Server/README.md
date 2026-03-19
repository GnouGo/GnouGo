# GnOuGo.Diff - Audit & Diff Viewer

GnOuGo.Diff is a complete solution for managing entity change history with visual diff comparison.

## 🎯 Features

- **Incremental storage**: Only stores the differences between versions
- **Full audit**: Change tracking with author and timestamp
- **Intuitive visualization**: Git-style diff interface to compare two versions
- **REST API**: Endpoints for integration into other applications
- **JSON support**: Perfect for serialized configurations

## 📦 Architecture

### GnOuGo.Diff.Core
.NET library containing:
- Data models (`DiffEntry`)
- Diff management service (`DiffService`)
- Entity Framework Core DbContext (SQLite)
- Uses **DiffPlex** for diff computation

### GnOuGo.Diff.Server
ASP.NET Core API + React ClientApp:
- **REST API** to create/read revisions
- **ClientApp** with visual comparison interface
- Uses **react-diff-viewer-continued** for display

### GnOuGo.Diff.Cli
Test CLI to insert sample data:
- `seed` command to populate the database
- Examples: configurations, user profiles, pricing

## 🚀 Quick Start

### 1. Build the project

```powershell
cd C:\github\GnOuGo.Agent
dotnet build
```

### 2. Start the API

```powershell
cd src\GnOuGo.Diff.Server
dotnet run
```

The API starts on **http://localhost:5100**

### 3. Install and build the ClientApp

```powershell
cd src\GnOuGo.Diff.Server\ClientApp
npm install
npm run build
```

### 4. Insert test data

In a new terminal:

```powershell
cd src\GnOuGo.Diff.Cli
dotnet run -- seed
```

### 5. Open the interface

Open **http://localhost:5100** in your browser

## 📖 API Endpoints

### Create a revision
```http
POST /api/revisions
Content-Type: application/json

{
  "entityType": "AppConfiguration",
  "entityId": "api-config",
  "currentValue": "{...}",
  "author": "user@example.com"
}
```

### List revisions for an entity
```http
GET /api/revisions/{entityType}/{entityId}
```

### Get a revision at a specific timestamp
```http
GET /api/revisions/{entityType}/{entityId}/at/{timestamp}
```

### Compare two revisions
```http
GET /api/revisions/compare/{fromId}/{toId}
```

### List entity types
```http
GET /api/entity-types
```

### List entities of a type
```http
GET /api/entities/{entityType}
```

## 💡 Usage

1. **Select an entity type** (e.g., AppConfiguration, UserProfile)
2. **Select an entity** (e.g., api-config)
3. **Choose two revisions** to compare (FROM = old, TO = new)
4. **View the diff** with statistics:
   - Added lines (green)
   - Removed lines (red)
   - Modified lines (orange)
   - Unchanged lines

## 🗂️ Data Model

```csharp
public class DiffEntry
{
    public long Id { get; set; }
    public string EntityType { get; set; }      // Entity type
    public string EntityId { get; set; }        // Unique entity ID
    public DateTimeOffset Timestamp { get; set; }
    public string Author { get; set; }
    public string CurrentValue { get; set; }    // Full value (JSON)
    public string? DiffFromPrevious { get; set; } // Diff vs previous version
    public string ValueHash { get; set; }       // SHA256 for deduplication
}
```

## 🔧 Technologies

- **.NET 10** - Backend
- **Entity Framework Core** - ORM
- **SQLite** - Database
- **DiffPlex** - Diff computation
- **React 18** - Frontend
- **Vite** - Build tool
- **react-diff-viewer-continued** - Visual diff component

## 📊 Sample Data

The CLI inserts 3 entity types with multiple revisions:

1. **AppConfiguration** (`api-config`)
   - 3 revisions showing the evolution of an API config
   - Changes: timeout, retries, version, endpoints

2. **UserProfile** (`user-123`)
   - 3 revisions showing the evolution of a user profile
   - Changes: email, role, permissions, preferences

3. **PricingConfiguration** (`prod-pricing`)
   - 2 revisions showing the addition of an Enterprise plan
   - Changes: Starter plan price, added features

## 🎨 Interface

The interface displays:
- **Revision list** with timestamp and author
- **FROM/TO selection** to choose 2 versions to compare
- **Statistics**: number of added/removed/modified lines
- **Side-by-side view** with JSON syntax highlighting
- **Navigation** by entity type and ID

## 🔒 Security

- Revisions are read-only (no deletion via API)
- SHA256 hash for duplicate detection
- UTC timestamps for global consistency

## 🚧 Development

### Development mode (hot reload)

```powershell
# Terminal 1: API
cd src\GnOuGo.Diff.Server
dotnet watch

# Terminal 2: ClientApp
cd src\GnOuGo.Diff.Server\ClientApp
npm run dev
```

ClientApp accessible at **http://localhost:5173** (with proxy to API)

### Add a new entity

```csharp
var request = new CreateRevisionRequest(
    EntityType: "MyType",
    EntityId: "my-id",
    CurrentValue: JsonSerializer.Serialize(myObject),
    Author: "user@example.com"
);

var revision = await diffService.CreateRevisionAsync(request);
```

## 📝 Notes

- Diffs are computed using the Myers algorithm (DiffPlex)
- The first revision of an entity has no `DiffFromPrevious`
- If two revisions have the same hash, the new one is not created
- `CurrentValue` is stored in full for each revision (no reconstruction)

## 🎯 Use Cases

- **Configuration audit**: Track application config changes
- **User history**: See the evolution of a profile
- **Pricing management**: Track pricing modifications
- **Compliance**: Answer "Who changed what and when?"
- **Rollback**: Retrieve a value at a specific point in time

---

**GnOuGo.Diff** is part of the **GnOuGo.Agent** project — A lightweight solution for observability and audit.
