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

## Admin Passkeys (MVP de autenticação)
Passkeys usam Fido2 4.0.1; somente os dois IDs de administrador pré-criados existem. O banco precisa receber a migration `202611110001_AdminPasskeys` (configure `Database__MigrateOnStartup=true` uma vez, ou execute `dotnet run --project src/Checkpoint.Api -- --migrate-only`). Não remova nem recrie as tabelas existentes.

Configure no backend:
| Variável | Obrigatória | Uso |
|---|---|---|
| `AdminAuth__BootstrapSecret` | durante configuração inicial | Segredo aleatório de pelo menos 32 bytes, por exemplo gerado com `openssl rand -base64 32`; nunca coloque no frontend ou repositório. |
| `AdminAuth__BootstrapExpiresUtc` | durante configuração inicial | Data ISO-8601 UTC curta para encerrar setup automaticamente. |
| `Fido2__ServerDomain` | produção | RP ID: hostname sem esquema, igual ao domínio usado para cadastrar a Passkey. Em desenvolvimento o padrão é `localhost`. |
| `Fido2__Origins__0` | produção | Origem HTTPS exata do site; localmente o padrão é `http://localhost:4321`. |
| `DataProtection__KeyRingPath` | produção | `/var/lib/checkpoint-data-protection`; o deploy mantém esse caminho em um volume Docker persistente. |

Local: rode a migration e inicie a API com `ASPNETCORE_ENVIRONMENT=Development`; o RP padrão é `localhost`. Em `Frontend`, defina `PUBLIC_API_URL` para a API local e inicie `npm run dev`. Abra `/admin/auth-test`, informe o segredo e cadastre a Passkey de Átila. Juh permanece pré-criada no banco, mas sem credencial até um setup futuro. Faça logout e teste o login. Em produção, o backend falha ao iniciar se faltarem RP ID e origem HTTPS explícita; CORS também exige origem definida. Configure esses valores e o caminho persistente de Data Protection no arquivo `.env` do servidor, nunca neste repositório. O exemplo de nomes está em `.env.example`.

O bootstrap é aberto enquanto houver ao menos um dos dois administradores sem credencial. Para este setup, cadastre apenas Átila e, assim que confirmar, remova `AdminAuth__BootstrapSecret` e `AdminAuth__BootstrapExpiresUtc` do `.env` do servidor e recrie o container; assim Juh continua sem Passkey e o cadastro fica fechado. Não inclua o segredo em GitHub Actions, no frontend ou no repositório. Os challenges expiram em 3 minutos, são associados a cookie HttpOnly e consumidos uma única vez; cache é volátil (pedidos em andamento podem ser reiniciados após restart). Sessões duram 2 horas, sem sliding expiration. Cookies de autenticação são HttpOnly/SameSite Strict, Secure em produção; operações autenticadas de escrita usam double-submit CSRF. O deploy monta um volume persistente para as chaves de Data Protection.

Antes do primeiro uso no servidor, aplique a migration `202611110001_AdminPasskeys` com o usuário PostgreSQL dono do schema `checkpoint`; a role mínima da API pode não ter permissão de DDL. Exemplo operacional no servidor (substitua a imagem e use a connection string do dono, sem a gravar no repositório): `docker run --rm --network shared-net --env-file /home/ubuntu/apps/checkpoint/.env -e 'ConnectionStrings__Checkpoint=CONNECTION_STRING_DO_DONO' ghcr.io/atilaalcantara/checkpoint-api:latest --migrate-only`. A migration adiciona apenas as tabelas administrativas e não recria tabelas existentes.

Rotas: `POST /api/admin/passkeys/register/options`, `POST /api/admin/passkeys/register/verify`, `POST /api/admin/auth/passkey/options`, `POST /api/admin/auth/passkey/verify`, `GET /api/admin/auth/me`, `POST /api/admin/auth/logout`, `GET /api/admin/auth/csrf` (protegida) e `GET /api/admin/auth/bootstrap-status`.

## Desenvolvimento
```sh
CHECKPOINT_TEST_ADMIN="Host=localhost;Username=dev;Password=...;Database=postgres" dotnet test Checkpoint.slnx
```
Os testes criam e removem um banco `checkpoint_test_*`. Em Development há Swagger em `/swagger`.
