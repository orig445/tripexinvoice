# TripEx.Api — C# .NET Backend

Standalone ASP.NET Core 8 Web API for TripEX AI. No Supabase dependency — runs with PostgreSQL, Oracle AI, and local/S3 storage.

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
| `Jwt:Secret` | `JWT_SECRET` | HMAC secret for JWT signing (min 32 chars) |
| `Jwt:Issuer` | — | Token issuer (default: `TripEx.Api`) |
| `Jwt:Audience` | — | Token audience (default: `TripEx.Client`) |
| `Jwt:ExpirationHours` | — | Token lifetime (default: 24) |
| `Oracle:ApiKey` | `ORACLE_API_KEY` | Oracle Generative AI key |
| `Oracle:UseCustomModel` | `ORACLE_USE_CUSTOM_MODEL` | `true` to route Milo's chat (not OCR) through the fine-tuned custom model below (default: `false` = Gemini) |
| `Oracle:CustomModelEndpoint` | `ORACLE_CUSTOM_MODEL_ENDPOINT` | Hosting-cluster endpoint URL for the fine-tuned Milo model |
| `Oracle:CustomModelId` | `ORACLE_CUSTOM_MODEL_ID` | Model OCID of the fine-tuned custom model |
| `Storage:Provider` | `STORAGE_PROVIDER` | `local` or `s3` (default: `local`) |
| `Storage:LocalPath` | `STORAGE_PATH` | Local storage directory (default: `./storage`) |
| `Storage:S3:BucketName` | `S3_BUCKET_NAME` | S3 bucket name |
| `Storage:S3:AccessKey` | `S3_ACCESS_KEY` | S3 access key |
| `Storage:S3:SecretKey` | `S3_SECRET_KEY` | S3 secret key |
| `Storage:S3:Region` | — | S3 region (default: `us-east-1`) |
| `Storage:S3:Endpoint` | — | Custom S3 endpoint (for MinIO, etc.) |

## Database Setup

Create the `user_credentials` table in your PostgreSQL database:

```sql
CREATE TABLE IF NOT EXISTS user_credentials (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    email TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_user_credentials_email ON user_credentials(email);
CREATE INDEX idx_user_credentials_user_id ON user_credentials(user_id);
```

## API Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `POST` | `/api/auth/register` | None | Register new user |
| `POST` | `/api/auth/login` | None | Login, returns JWT |
| `POST` | `/api/chat` | JWT | Chat + Image scanning |
| `POST` | `/api/invoice/analyze` | JWT | Direct invoice OCR |
| `POST` | `/api/knowledge/process` | JWT | Process knowledge document |
| `GET` | `/api/health` | None | Health check |

## Authentication

The API uses standard HMAC-SHA256 JWT tokens. After login/register, include the token in requests:

```
Authorization: Bearer <token>
```

## Project Structure

```
TripEx.Api/
├── Controllers/
│   ├── AuthController.cs             # POST /api/auth/register, /api/auth/login
│   ├── ChatController.cs             # POST /api/chat
│   ├── InvoiceController.cs          # POST /api/invoice/analyze
│   ├── KnowledgeController.cs        # POST /api/knowledge/process
│   └── HealthController.cs           # GET /api/health
├── Services/
│   ├── AuthService.cs                # Registration, login, JWT generation
│   ├── FileStorageService.cs         # Local filesystem or S3 storage
│   ├── OracleAiService.cs            # Oracle AI calls + JSON parsing
│   ├── ChatService.cs                # Session mgmt, RAG, intent detection
│   ├── InvoiceService.cs             # OCR extraction + verification
│   ├── KnowledgeService.cs           # Document chunking for RAG
│   └── GeolocationService.cs         # IP-based location detection
├── Models/
│   └── ApiModels.cs                  # All request/response DTOs
├── Data/
│   └── TripExDbContext.cs            # EF Core DbContext + entities
├── Program.cs                        # App configuration
├── appsettings.json                  # Settings
└── TripEx.Api.csproj                 # Project file
```

## Connecting to Frontend

Set `VITE_API_BASE_URL` in the frontend:
```
VITE_API_BASE_URL=https://your-api-domain.com
```

## TODO

- [ ] Add PDF text extraction (iTextSharp)
- [ ] Add DOCX text extraction (DocumentFormat.OpenXml)
- [ ] Add rate limiting middleware
- [ ] Add response caching for knowledge base searches
- [ ] Add Docker support
