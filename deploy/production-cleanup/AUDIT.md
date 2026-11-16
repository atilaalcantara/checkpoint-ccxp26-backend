# Frente D — auditoria PRD, somente leitura

## Execução autorizada — backup e ensaio de 2026-11-16

**Backup/restauração/ensaio concluídos. Cleanup em PRD continua NÃO autorizado.**
O usuário informou HLG validada em navegador, todos os fluxos, e autorizou
expressamente backup PRD read-only e cleanup somente no clone efêmero.
Este registro não substitui os documentos de validação HLG de outra frente.

- Execução aprovada: **17:55:49–17:56:04 UTC**. Diagnóstico PRD:
  **2026-11-16 14:55:53.393216-03**, `personal_db`, `postgres`, read-only `on`.
- Pacote usado: worktree `ccxp-backend-hlg`, `deploy/production-cleanup`:
  `backup.sh`, `remote.sh dry-run`, `diagnostic.sql`, `cleanup.sql`, `postcheck.sql`.
  `remote.sh cleanup` **não foi chamado**.
- Origem: Docker host Oracle, `personal-postgres`, PostgreSQL **16.14**.
  SQL PRD em `REPEATABLE READ READ ONLY`/`default_transaction_read_only=on`;
  `pg_dump` custom com `PGOPTIONS` read-only. Sem DDL/DML ou mudança de status PRD.
- Backup privado verificado (caminho absoluto):
  `/Users/atilaalcantara/Documents/pixelArtCCXP26/checkpointCCXP26/backups-private/checkpoint-20261116T175551Z-bcKa1C`.
  Parent conferido com `ls`; destino fora de Git (`git rev-parse` rejeita worktree).
  Diretórios **700**, todos os arquivos **600**, verificados ao final.
- `checkpoint.dump`: schema `checkpoint` completo, incluindo originais/thumbnails
  `bytea` e todas as colunas privadas de autenticação. `pg_restore --list` passou;
  as 14 entradas TABLE DATA coincidem com todas as tabelas do schema na origem.
  `SHA256SUMS` final validou os 11 artefatos, incluindo dump, catálogo, logs,
  resumo de verificação e key ring. Nenhum conteúdo de credencial/hash foi publicado.
- `keyring-prd.tar`: leitura via `docker exec checkpoint-api tar` do mount
  `/var/lib/checkpoint-data-protection` (volume `checkpoint-data-protection`).
  Tar privado validado sem extração: caminhos seguros, XMLs de chaves parseáveis,
  conteúdo idêntico em segunda leitura. Sem imprimir nomes/conteúdo das chaves.
  Não inclui `.env`, roles globais PostgreSQL nem configuração completa da API.

### Contagens atuais e dry-run read-only

| Tabela | PRD / clone restaurado antes | Clone após cleanup |
|---|---:|---:|
| polls | 3 | 0 |
| poll_options | 6 | 0 |
| poll_assignments | 27 | 0 |
| votes | 15 | 0 |
| photos | 4 | 0 |
| photo_submissions | 5 | 0 |
| idempotency_operations | 0 | 0 |
| result_snapshots | 0 | 0 |
| visitors | 11 | 11 |
| admin_users | 2 | 2 |
| admin_passkeys | 2 | 2 |
| site_status | 1 | 1 |
| __EFMigrationsHistory | 4 | 4 |
| community_progress | 1 | 1 |

PRD: **25 XP, revisão 20, status `before`**; 15 XP de votos + 10 XP de
marcadores, resíduo zero. Fotos: **4.853.745 bytes** originais e **49.651 bytes**
thumbnails. Um marcador sem foto; zero fotos sem marcador, votos órfãos ou
enquetes fora da regra de duas opções. Dez FKs validadas e os dois constraint
triggers presentes. `remote.sh dry-run` executou somente diagnóstico read-only.

### Restauração, comparação e cleanup exclusivo no clone

- Docker local indisponível; clientes PG16 executados no Docker host via SSH.
  Adaptador temporário de `pg_restore` aceita somente `--version`/`--list`, usa
  container cliente `--rm --network none --read-only` e stdin, sem conexão SQL.
- Clone **`checkpoint-restore-frente-d-8000cafc27eb`**, banco **`personal_db`**
  exclusivamente nele. Imagem PG16 já existente, fixada por ID:
  `sha256:00ba258a66dac104fd5171074a0084462a64a1369d8513f3d0a634e2f24d15bc`.
  `--network none`, sem portas, sem mounts de volumes/binds, rootfs read-only,
  PGDATA e socket em `tmpfs`. Sem API/workers ou rede PRD/HLG.
  Inspeção de nome, label exclusiva, rede e ausência de volumes antes do cleanup.
- Restore: `pg_restore --single-transaction --exit-on-error --no-owner --no-acl`,
  sem `--clean`, diretamente em `docker exec` no clone. Exit code zero.
- Contagens e SHA-256 interno de **todas as linhas/colunas das 14 tabelas** iguais
  entre origem e restore. Serialização `to_jsonb`, timestamps normalizados a UTC,
  DateStyle ISO/YMD, bytea hex, ordenação `COLLATE "C"`; inclui todos os bytes
  das fotos e todos os campos de admins/passkeys. Hashes apenas em memória do
  verificador, resultados externos somente booleanos de igualdade.
  Origem igual antes/depois do backup e depois do ensaio; nenhuma divergência.
- `cleanup.sql` original executado com **COMMIT real somente no clone**.
  `SET CONSTRAINTS ALL IMMEDIATE`, assertions internas e `postcheck.sql` passaram.
  Oito tabelas-alvo vazias, **XP 0, revisão 21**, status `before`.
  Igualdade integral pós-cleanup de visitantes, admins (incluindo role), passkeys
  (incluindo credenciais/counters), status e migrações confirmada; constraints
  do schema todas validadas. Não é teste de login nem backup de roles globais PG.
- Container removido por nome/label exatos ao final, ausência confirmada.
  Nenhum volume persistente foi criado; PGDATA/socket tmpfs descartados.
  Nenhum container/volume PRD ou HLG removido/reiniciado.

Evidências privadas no bundle: `diagnostic-prd.log`, `dry-run-prd.log`,
`diagnostic-clone-before.log`, `restore.log`, `cleanup-clone.log`,
`postcheck-clone.log`, `verification-summary.json`, além de dump/catálogo/keyring.
Orquestradores locais temporários: `frente-d-rehearsal.py`,
`frente-d-final-check.py` e adaptador `pg_restore` no diretório temporário OpenCode.

**Tentativa preliminar segura:** bundle privado
`checkpoint-20261116T175518Z-rh4unl`, 17:55:16–17:55:29 UTC. Restore passou e
contagens coincidiram, mas a primeira comparação serializou timestamps com
timezones diferentes na origem e no clone. A comparação bloqueou o cleanup;
container `checkpoint-restore-frente-d-f815c5a8adfb` removido. Bundle mantido como
evidência preliminar, **não usar como backup verificado**. A segunda execução
acima normalizou UTC e passou integralmente, com backup novo.

**Gate restante:** nenhuma autorização de exclusão PRD foi dada. Este backup
prova restaurabilidade neste snapshot; futuras escritas exigem reavaliar janela,
diagnóstico e backup antes de eventual limpeza autorizada. Sem commit nesta etapa.

## Registro histórico anterior (não representa o resultado acima)

## Execução e proveniência

- Snapshot: **2026-11-16 13:46:59.745808-03** (16:46:59 UTC).
- Origem: auditoria previamente registrada em `ccxp-cleanup/AUDIT.md`; persistida
  no backend em `deploy/production-cleanup`. Não é nova consulta a PRD.
- Host `ubuntu@oracle.atilaalcantara.com`, chave referenciada por caminho
  `~/.ssh/ssh-key-2026-05-22.key`; BatchMode/StrictHostKeyChecking/IdentitiesOnly.
- Container `personal-postgres`, usuário `postgres`, banco `personal_db`,
  PostgreSQL 16.14. Nenhuma senha foi impressa ou copiada.
- Sessão SQL confirmou `transaction_read_only=on`. Consultas em `REPEATABLE READ
  READ ONLY`, com `default_transaction_read_only=on` no cliente; apenas agregados
  e metadados, sem valores de IDs, admins, credenciais, imagens ou JSON individual.
- Código auditado originalmente em
  `/Users/atilaalcantara/Documents/pixelArtCCXP26/checkpointCCXP26/Backend`, já sujo.
  Referências de código abaixo são históricas, não certificação da versão HLG.
- Imagem observada em PRD: `checkpoint-api:passkey-invite-20261111`. Código local
  não foi extraído/validado contra o binário da imagem. Schema/FKs/triggers e
  contagens foram confirmados na auditoria original em PRD.
- Nenhum backup, DELETE, UPDATE, DDL, migração, reinício ou alteração de configuração
  foi executado pela frente. Scripts destrutivos foram somente preparados.
- **HLG ainda em publicação: concluir e validar HLG antes de solicitar backup;
  restore verificado e nova confirmação explícita do usuário antes da exclusão.**

## Contagens observadas (snapshot, não estado atual garantido)

| Objeto checkpoint | Linhas | Tratamento preparado |
|---|---:|---|
| polls | 3 | excluir todas as enquetes, não apenas demo |
| poll_options | 6 | excluir |
| poll_assignments | 27 | excluir para permitir nova distribuição |
| votes | 15 | excluir; remover 15 XP |
| photos | 4 | excluir original e thumbnail |
| photo_submissions | 5 | excluir marcadores; remover 10 XP |
| idempotency_operations | 0 | limpar possíveis respostas antigas |
| result_snapshots | 0 | limpar possíveis resultados antigos |
| visitors | 11 | preservar identidades anônimas |
| admin_users | 2 | preservar integralmente |
| admin_passkeys | 2 | preservar integralmente |
| site_status | 1 | preservar; estado observado `before` |
| __EFMigrationsHistory | 4 | preservar |
| community_progress | 1 | atualizar singleton, não excluir |

As 3 enquetes são ativas, visíveis e não demo. Fotos: **4.853.745 bytes de
originais** e **49.651 bytes de thumbnails** (4.903.396 bytes de payload; não é
estimativa do espaço físico recuperável).

## XP: decisão essencial

`total_xp=25`, `revision=20`:

```
votos:              15 × 1 XP = 15
envios históricos:   5 × 2 XP = 10
total explicado:               25
resíduo não explicado:          0
após limpeza completa:          0 XP / revisão 21
```

Há **1 marcador sem foto armazenada**, **0 fotos sem marcador**.
`AdminManagement.cs:77-81` apaga somente fotos, mantendo marcador e XP; compatível
com moderação anterior, sem provar a causa histórica. Usar somente `count(photos)`
descontaria 8 XP e deixaria 2 XP indevidos. O ledger é `photo_submissions`.
Não usar a rota administrativa de excluir fotos para este reset.

`ParticipationService.cs:58-62` concede 1 XP/voto; `:81-88` insere foto/marcador e
concede 2 XP atomicamente; `:97-101` incrementa revisão por concessão.
`20 = 15 votos + 5 envios` é consistente. O script reconcilia XP sob locks e
aborta se houver resíduo. Zera XP e incrementa revisão, sem resetar a revisão,
para aceitação do novo estado pelos clientes (`Program.cs:197-204`).

## Dependências e referências

10 FKs validadas, sem `ON DELETE CASCADE`:

```
votes -> poll_assignments (visitor_id,poll_id)
votes -> poll_options (poll_id,option_id)
poll_assignments -> polls, visitors
poll_options -> polls
photos -> visitors
photo_submissions -> visitors
idempotency_operations -> visitors
result_snapshots -> visitors
admin_passkeys -> admin_users [ramo separado, preservado]
```

Nenhuma FK externa apontando para checkpoint encontrada. Dependências normais
em `pg_depend` eram constraints, sem view de aplicação dependente. São 14 tabelas
comuns sem RLS. Duas views internas de estatísticas PostgreSQL apareceram por
coincidência textual com "checkpoint", não como dependências de aplicação.

Triggers `poll_has_two_options` e `options_remain_two`, ambos `DEFERRABLE INITIALLY
DEFERRED`, usam `checkpoint.check_poll_options()` para exigir duas opções/enquete.
Ordem: snapshots/idempotência → votos → atribuições → opções → enquetes → fotos →
marcadores. `SET CONSTRAINTS ALL IMMEDIATE` valida antes do COMMIT. Sem CASCADE,
TRUNCATE, desativação de trigger ou DROP de schema.

Integridade: 0 enquetes fora da regra de 2 opções, 0 votos sem atribuição ou opção.
`ResultsService.cs` consulta votos atuais. `idempotency_operations` e
`result_snapshots` apareceram somente no schema na busca local, mas serão limpos
para não manter payloads/referências legados.

## Preservação de autenticação/configuração

- `admin_users`, `admin_passkeys` (credential_id, public_key, user_handle,
  transports, sign_count), status, migrações e visitantes: nenhuma escrita
  planejada. Cópias transacionais internas comparam todas as colunas antes do
  COMMIT, sem selecionar valores para o cliente.
- `Program.cs:27-42`: cookies dependem de Data Protection. Metadata Docker
  confirmou montagem na API em `/var/lib/checkpoint-data-protection`.
  Não remover/recriar volume nem reiniciar a API neste procedimento.
- Foram inspecionados apenas nomes de variáveis da API, não valores:
  `DataProtection__KeyRingPath`, `ConnectionStrings__Checkpoint`,
  `Fido2__ServerDomain`, `Fido2__Origins__0`, `Cors__Origins__0`,
  `Database__MigrateOnStartup`, além de runtime/proxy.
- Preservar Fido2/RP ID/origins, CORS, conexão, thresholds, assets privados,
  proxy e key ring. Dump SQL não substitui cópia privada de configuração/volume.
- `AdminAuth.cs:99-118` atualiza `sign_count` no login; locks podem bloqueá-lo
  brevemente. Não reverter counters/credenciais com restore indiscriminado.
- Challenges/convites estão em cache de memória no código local. SQL não os toca.
  Não houve teste de login/cadastro/convite, pois produziriam escritas.

## Validação feita e limites

- Na auditoria original, `diagnostic.sql` executou com sucesso em PRD somente leitura;
  `bash -n remote.sh backup.sh` passou. Não confundir com nova execução de PRD.
- `cleanup.sql`: revisão estática; não executado, nem com ROLLBACK.
- Persistência revisa backup para destino privado fora do Git, permissões `600`,
  caminho remoto independente de cwd, checksum e catálogo `pg_restore --list`.
  O dump cobre bytea/fotos e passkeys; não a chave privada do autenticador,
  key ring, ambiente ou outros schemas. Ver estratégia sandbox no README.
- Nenhum backup/restauração ou ensaio transacional foi validado nesta etapa.
  Reauditar a versão/schema publicados, verificar restore em PostgreSQL 16
  isolado e obter nova confirmação explícita antes da futura limpeza.
- Exclusão é lógica/transacional, sem apagar WAL, backups, páginas mortas ou
  caches/browser. Sem VACUUM FULL ou expurgo de backups no escopo.
