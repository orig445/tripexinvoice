# TripEx.Api — C# .NET Backend

Ready-to-run ASP.NET Core 8 Web API for TripEX AI.

## Quick Start

```bash
cd TripEx.Api
dotnet restore
dotnet run
```

The API will start at `https://localhost:5001` (or `http://localhost:5000`).

Swagger UI: `http://localhost:5000/swagger`

## Configuration

Edit `appsettings.json` or set environment variables:

| Setting | Env Variable | Description |
|---------|-------------|-------------|
| `ConnectionStrings:DefaultConnection` | `DATABASE_URL` | PostgreSQL connection string |
| `Supabase:Url` | `SUPABASE_URL` | Supabase project URL (for JWT validation) |
| `Supabase:ServiceRoleKey` | `SUPABASE_SERVICE_ROLE_KEY` | For storage access |
| `Oracle:ApiKey` | `ORACLE_API_KEY` | Oracle Generative AI key |

## API Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `POST` | `/api/chat` | JWT | Chat + Image scanning |
| `POST` | `/api/invoice/analyze` | JWT | Direct invoice OCR |
| `POST` | `/api/knowledge/process` | JWT | Process knowledge document |
| `GET` | `/api/health` | None | Health check |

## Project Structure

```
TripEx.Api/
├── Controllers/
│   ├── ChatController.cs          # POST /api/chat
│   ├── InvoiceController.cs       # POST /api/invoice/analyze
│   ├── KnowledgeController.cs     # POST /api/knowledge/process
│   └── HealthController.cs        # GET /api/health
├── Services/
│   ├── OracleAiService.cs         # Oracle AI calls + JSON parsing
│   ├── ChatService.cs             # Session mgmt, RAG, intent detection
│   ├── InvoiceService.cs          # OCR extraction + verification
│   ├── KnowledgeService.cs        # Document chunking for RAG
│   └── GeolocationService.cs      # IP-based location detection
├── Models/
│   └── ApiModels.cs               # All request/response DTOs
├── Data/
│   └── TripExDbContext.cs         # EF Core DbContext + entities
├── Program.cs                     # App configuration
├── appsettings.json               # Settings
└── TripEx.Api.csproj              # Project file
```

## Connecting to Frontend

Set `VITE_API_BASE_URL` in the frontend `.env`:
```
VITE_API_BASE_URL=https://your-api-domain.com
```

## TODO

- [ ] Implement file download in `KnowledgeService` (from Supabase Storage)
- [ ] Add rate limiting middleware
- [ ] Add response caching for knowledge base searches
- [ ] Add Docker support
