# Frente D — auditoria PRD, somente leitura

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
