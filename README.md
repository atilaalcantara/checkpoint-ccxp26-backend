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

## Autenticação do futuro painel administrativo

A Passkey de produção de Átila já foi cadastrada. Cadastro inicial, segredo de bootstrap e página de testes foram removidos; não existe endpoint público para adicionar credenciais. Somente a credencial vinculada a Átila é aceita para novos logins. As tabelas `checkpoint.admin_users` e `checkpoint.admin_passkeys`, criadas pela migration `202611110001_AdminPasskeys`, devem ser preservadas, assim como o volume Docker `checkpoint-data-protection`. Não exclua a credencial existente: não há recuperação automática nem cadastro substituto implementado.

Contrato para implementar o futuro painel, na mesma origem HTTPS do site:
1. `POST /api/admin/auth/passkey/options` gera opções WebAuthn para login descobrível com `userVerification: required` e envia cookie HttpOnly temporário. Converta `challenge` e IDs de credenciais de base64url para bytes, e passe as opções a `navigator.credentials.get({ publicKey: options })`.
2. `POST /api/admin/auth/passkey/verify` recebe o `PublicKeyCredential` serializado em JSON: `id`, `rawId`, `type`, `clientExtensionResults` e `response` com `clientDataJSON`, `authenticatorData`, `signature` e `userHandle` quando presente; os campos binários devem ser base64url. A verificação WebAuthn ocorre no backend, que emite cookie HttpOnly `checkpoint.admin`.
3. `GET /api/admin/auth/me` retorna `id`, `name` e `role` da sessão, ou 401. Use `credentials: 'include'` nas requisições.
4. Antes de qualquer operação autenticada de escrita, solicite `GET /api/admin/auth/csrf` e envie o `token` retornado no header `X-CSRF-Token` da requisição; isso inclui `POST /api/admin/auth/logout`. Após logout, a sessão não deve mais ser usada.

Cookie: HttpOnly, SameSite Strict, Secure em produção, duração de 2 horas sem renovação automática. Os challenges expiram após 3 minutos, são consumidos uma só vez e ficam em memória na instância da API. O RP ID de produção deve continuar sendo o domínio em que a Passkey foi criada; uma credencial de `localhost` não funciona em produção. Configure `Fido2__ServerDomain`, `Fido2__Origins__0` e `Cors__Origins__0` com o domínio/origem corretos; `DataProtection__KeyRingPath=/var/lib/checkpoint-data-protection` deve apontar ao volume persistente. Login usa Fido2 4.0.1; não há senha, JWT ou recuperação automática.

O deploy aplica migrations antes de substituir a API. Não remova o volume nem as tabelas administrativas nos próximos deploys.

## Desenvolvimento
```sh
CHECKPOINT_TEST_ADMIN="Host=localhost;Username=dev;Password=...;Database=postgres" dotnet test Checkpoint.slnx
```
Os testes criam e removem um banco `checkpoint_test_*`. Em Development há Swagger em `/swagger`.
