# Checkpoint CCXP26 — Backend

API em ASP.NET Core (.NET 10) + PostgreSQL. Consumida pelo [frontend](https://github.com/atilaalcantara/checkpoint-ccxp26-frontend).

## Como funciona
- **Participação** (`PUT /api/v1/participation`): o visitante (UUID v4 no header `X-Visitor-Id`) recebe 3 enquetes sorteadas, fixas daí em diante.
- **Voto** (`POST /api/v1/votes`): validado no servidor (enquete atribuída, opção válida, um voto por enquete) dentro de uma transação com lock por visitante, o que impede voto duplicado mesmo em requisições simultâneas. Vale 1 XP.
- **Foto** (`POST /api/v1/photos`): uma por visitante, vale 2 XP. O formato é detectado pelo conteúdo do arquivo (JPEG, PNG, WebP, AVIF, HEIC, JPEG 2000), com limite de 10 MiB e 40 MP. A miniatura pública (360x330 AVIF, sem EXIF) é gerada na hora; o original fica no Postgres (`bytea`) para impressão e nunca é servido.
- **XP/nível**: contador único da comunidade (`community_progress`), só cresce. Apagar uma foto não remove o XP.
- **Resultados** (`GET /api/v1/results`): liberados após 3 votos.
- **Saúde**: `/health/live` (processo) e `/health/ready` (banco + codec de imagem).

## Segurança já embutida
Rate limit por IP (429 + `Retry-After`), headers `nosniff`/`Referrer-Policy`/CSP/HSTS, CORS só com origens explícitas, erros genéricos sem detalhes internos, logs sem payload nem IDs, timeouts de banco (conexão 5 s, comando 15 s; falha vira 503).

## Variáveis de ambiente
| Variável | Padrão | Para quê |
|---|---|---|
| `ConnectionStrings__Checkpoint` | — (obrigatória) | conexão com seu Postgres |
| `Cors__Origins__0` | localhost | origem do frontend (omitir se usar proxy na mesma origem) |
| `Database__MigrateOnStartup` | `false` | aplica migrations ao subir |
| `Proxy__TrustForwardedHeaders` | `false` | `true` só se a API for acessível apenas pelo proxy (que deve sobrescrever `X-Forwarded-For`) |
| `RateLimit__RequestsPerMinute` / `VotesPerMinute` / `UploadsPerMinute` | 1800 / 300 / 60 | limites por IP |
| `Images__MaxFileBytes` / `MaxPixels` / `Concurrency` | 10 MiB / 40 MP / 2 | upload |
| `Progression__FriendsAtXp` … `MarriedAtXp` | 15 / 55 / 130 / 230 | XP de cada nível |

## Deploy (Linux ARM64)
```sh
docker buildx build --platform linux/arm64 -t checkpoint-api .
# 1) migrations, com o usuário dono do banco
docker run --rm -e "ConnectionStrings__Checkpoint=Host=...;Database=checkpoint;Username=...;Password=..." checkpoint-api --migrate-only
# 2) API, com usuário de privilégio mínimo (ver abaixo), só atrás do proxy TLS
docker run -d --restart unless-stopped -p 127.0.0.1:8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production -e "ConnectionStrings__Checkpoint=..." \
  -e Cors__Origins__0=https://seu-dominio -e Proxy__TrustForwardedHeaders=true checkpoint-api
```
Usuário de banco mínimo (a API nunca apaga dados):
```sql
CREATE ROLE checkpoint_app LOGIN PASSWORD '...';
GRANT CONNECT ON DATABASE checkpoint TO checkpoint_app;
GRANT USAGE ON SCHEMA checkpoint TO checkpoint_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA checkpoint TO checkpoint_app;
```
O Postgres não deve ter porta pública; o TLS termina no proxy (veja o `nginx.conf.example` do frontend); bloqueie `/health` ao público e use `/health/ready` para monitorar.

## Operação
- **Pedido de exclusão de foto**: `DELETE FROM checkpoint.photos WHERE id = '<id>';` (o id aparece em `/api/v1/photos/recent`).
- **Backup**: `pg_dump` periódico; as fotos estão no banco (até ~10 MiB cada), acompanhe `pg_database_size`.
- **Limites conhecidos**: identidade do visitante é um UUID gerado no cliente (sem login) e não há moderação.

## Desenvolvimento
```sh
CHECKPOINT_TEST_ADMIN="Host=localhost;Username=dev;Password=...;Database=postgres" dotnet test Checkpoint.slnx
```
Os testes criam e removem um banco `checkpoint_test_*`. Em Development há Swagger em `/swagger`.
