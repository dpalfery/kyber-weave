---
name: build-commands
description: Build, run, and test commands for the MotorcycleRAG solution. Use when you need to build, run the API, run tests, or start the frontend dev server.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Build & Run Commands

## Build (with analyzers)
```powershell
dotnet build -c Debug -p:Platform="Any CPU" -p:EnforceCodeStyleInBuild=true -p:EnableNETAnalyzers=true
```

## Run API
```powershell
dotnet run --project 1-Presentation/MotorcycleRAG.API
```

## Run Tests
```powershell
dotnet test
```

## Frontend Dev Server
```powershell
cd 1-Presentation/MotorcycleRag.WebUI
npm run dev
```

## Data Ingestion Endpoints
- Upload: `POST /api/DataPipeline/upload`
- Process: `POST /api/DataPipeline/process`
