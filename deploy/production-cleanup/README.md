# Frente D — preparação de limpeza de produção

**BLOQUEADO: HLG ainda em publicação. Backup e limpeza NÃO executados.**
Pacote persistido em `deploy/production-cleanup`, a partir de `ccxp-cleanup`
temporário. Apenas scripts e documentação públicos; nenhum dump, credencial,
chave SSH, `.env` ou key ring pertence ao repositório.

## Snapshot e escopo

Auditoria histórica: **2026-11-16 13:46:59.745808-03**, PostgreSQL 16.14.
**3 enquetes, 15 votos, 4 fotos, 5 marcadores de envio, 25 XP, 2 admins,
2 passkeys**. Há 11 visitantes, 6 opções, 27 atribuições e 4 migrações.
Revisão 20, site `before`. Não são contagens atuais garantidas; ver `AUDIT.md`.

Excluir todas as enquetes/opções/atribuições/votos/fotos/marcadores e derivados
(`result_snapshots`, `idempotency_operations`); zerar XP, incrementar revisão.
Preservar integralmente visitantes, admins, passkeys, status, migrações,
configuração e volume de Data Protection. Os **5 marcadores**, não as 4 fotos,
explicam 10 XP; junto dos 15 votos explicam os 25 XP.

## Arquivos e caminhos

- `diagnostic.sql`: transação `REPEATABLE READ READ ONLY`; metadados/agregados.
- `dry-run.sql`: inclui o diagnóstico; não simula DML nem triggers.
- `remote.sh`: diagnóstico padrão, dry-run, postcheck e modo cleanup protegido.
- `cleanup.sql`: **destrutivo, com COMMIT real**; locks, reconciliação de XP,
  validação de FKs e comparação de todas as colunas preservadas antes do COMMIT.
- `backup.sh`: futuro dump custom privado, checksum e inspeção do catálogo.
- `postcheck.sql`: assertions read-only do estado após uma eventual limpeza.
- `AUDIT.md`: evidências da auditoria original e limites.

Executar scripts com `bash`. `remote.sh` resolve os arquivos SQL por caminho
local absoluto relativo ao próprio script. Não é necessário copiar o pacote ao
servidor. O caminho remoto é **SSH `ubuntu@oracle.atilaalcantara.com` →
`docker exec personal-postgres` → banco PRD `personal_db`, schema `checkpoint`**,
independente do diretório de login SSH. **Não é `checkpoint_hlg`**. A chave é
apenas referenciada pelo caminho local `~/.ssh/ssh-key-2026-05-22.key`;
seu conteúdo nunca é copiado por este pacote. SSH sem PTY (`-T`),
`BatchMode`, `StrictHostKeyChecking` e `IdentitiesOnly` ativados.

## Diagnóstico / dry-run somente leitura

No diretório deste pacote:

```bash
bash remote.sh diagnostic
bash remote.sh dry-run
```

Alternativa local: `psql -X -v ON_ERROR_STOP=1 -f dry-run.sql` com conexão
explicitamente selecionada. Nenhum modo read-only executa `cleanup.sql`, nem
mesmo com rollback. Não chamar `postcheck` antes da limpeza esperando sucesso.

## Sequência futura obrigatória — não executar durante a publicação de HLG

1. **Concluir e validar HLG primeiro**, registrando resultado e versão validada.
   Só depois, mediante solicitação do usuário, criar o backup de PRD. As flags
   abaixo são atestações do operador, não validação automática de HLG ou restore.
2. Coordenar janela sem votos/uploads/administração/login concorrentes e manter
   o site não-live. O pacote não muda `site_status` nem reinicia a API. Refazer
   diagnóstico e revisar schema/FKs/triggers contra a versão realmente publicada.
   Manter a janela sem escritas durante diagnóstico, backup, verificação e limpeza;
   se houver escritas, renovar evidências/backup. Timeout de lock aborta o SQL.
3. **Após validação HLG e solicitação de backup**, preparar um diretório privado
   existente fora de qualquer repositório/sincronização pública. Exemplo futuro:

   ```bash
   HLG_VALIDATED=yes CONFIRM_BACKUP=EXPORT_CHECKPOINT \
     BACKUP_DIR="$HOME/private-checkpoint-backups" bash backup.sh
   ```

   O diretório precisa existir. O script recusa destino dentro de worktree Git,
   aplica `umask 077` e `chmod 700` aos diretórios, `chmod 600` a dump, catálogo,
   log e checksums. Requer clientes PostgreSQL 16, Git, SSH e `shasum`. Não usar
   `bash -x`, CI com artefatos públicos ou terminal gravado para conteúdo privado.
   Erros SSH/pg_dump ficam em log privado, nunca são impressos pelo script.
   Falhas retêm bundle **INCOMPLETE**, sem autorização para usar na limpeza.

### O que o backup cobre

`pg_dump --schema=checkpoint --strict-names --format=custom --no-owner --no-acl`
exporta estrutura e dados de **todas** as tabelas do schema, incluindo:

- `photos.original` e `photos.thumbnail`: `bytea`/TOAST incluídos como dados
  comuns de tabela, sem conversão de imagem nem necessidade de `--blobs`;
- todos os campos de `admin_users` e `admin_passkeys`, incluindo `credential_id`,
  `public_key`, `user_handle`, transports e `sign_count`;
- marcadores de envio, visitantes, XP, revisão, status e histórico de migrações.

Mesmo que a chave de autenticação WebAuthn seja **pública no servidor** (a chave
privada do autenticador não fica no PostgreSQL), dump e registros de autenticação
são privados/sensíveis. Não publicar nem versionar dump, hashes de dados, catálogo
ou logs. O dump não cobre roles globais, grants originais, extensões fora do schema,
outros schemas, ambiente/API, chave privada SSH ou **key ring Data Protection**.
Não remover/recriar o volume `checkpoint-data-protection` montado na API em
`/var/lib/checkpoint-data-protection`; sua cópia privada requer procedimento
separado autorizado. O dump isoladamente não recupera cookies/configuração.

### Verificação obrigatória em sandbox PostgreSQL 16

4. No bundle privado: `shasum -a 256 -c SHA256SUMS`. O script já executa
   `pg_restore --list` e exige entradas `TABLE DATA` de fotos, marcadores, admins
   e passkeys. Revisar também as demais tabelas, funções, constraints e triggers.
   **Catálogo e checksum não provam restaurabilidade.**
5. Provisionar PostgreSQL 16 **isolado**, sem exposição pública e sem API/workers,
   banco vazio e sem conexão a PRD/HLG. Usar instância/volume descartável privado.
   Configurar previamente serviço libpq `checkpoint_restore_sandbox` apontando
   apenas para essa instância, com arquivos de credenciais em modo `600`. Conferir
   host, porta e banco antes de qualquer restore. Exemplo futuro, dentro do bundle:

   ```bash
   umask 077
   pg_restore --dbname='service=checkpoint_restore_sandbox' \
     --single-transaction --exit-on-error --no-owner --no-acl \
     checkpoint.dump > restore.log 2>&1
   chmod 600 restore.log
   ```

   Não usar `--clean` nem apontar para PRD, HLG ou `personal_db` compartilhado.
   A base sandbox pode ter nome diferente; o SQL destrutivo exige `personal_db`,
   portanto um ensaio dele exige banco com esse nome **somente na instância
   isolada**, sem alterar a proteção do script para executar em ambientes reais.
6. Rodar `diagnostic.sql` na conexão sandbox e comparar com o diagnóstico
   imediatamente anterior ao backup: todas as contagens, status, XP/revisão,
   FKs, triggers, zero resíduos de XP e bytes das imagens. Referência histórica:
   **4.853.745 bytes originais + 49.651 bytes thumbnails**. Durante janela sem
   escritas, verificar também igualdade de conteúdo: gerar manifestos privados
   ordenados por PK/identidade de todas as tabelas na origem e sandbox, contendo
   SHA-256 de cada linha serializada canonicamente (incluindo todos os campos de
   passkeys e hashes de original/thumbnail); comparar sem imprimir os dados.
   Contagens e tamanhos iguais, sozinhos, não demonstram fidelidade dos bytes.
   Guardar manifestos e resultados em modo `600`, fora do repo. Não declarar
   `BACKUP_VERIFIED=yes` até restore sem erro e comparações aprovadas.
7. Após restore verificado, ensaiar `cleanup.sql` **apenas nesse clone isolado**;
   rodar `postcheck.sql` e conferir preservação integral das tabelas protegidas.
   Essa etapa é destrutiva no clone e não faz parte do dry-run read-only.
8. **Pedir nova confirmação explícita do usuário antes de qualquer exclusão
   em PRD**, apresentando evidência HLG, backup/restauração e impacto atualizado.
   A solicitação de preparar este pacote NÃO autoriza backup nem exclusão.
   Só então, em janela ainda válida, o comando futuro seria:

   ```bash
   HLG_VALIDATED=yes BACKUP_VERIFIED=yes \
     CONFIRM_CLEANUP=DELETE_POLLS_VOTES_UPLOADED_PHOTOS bash remote.sh cleanup
   ```

9. Reexecutar diagnóstico e `bash remote.sh postcheck` (read-only). Esperado com
   o snapshot histórico inalterado: 8 tabelas-alvo vazias, 0 XP/revisão 21,
   11 visitantes, 2 admins, 2 passkeys, 4 migrações e site `before`. Comparar
   sempre com evidência atual, não fixar esse snapshot como garantia futura.
   Login/passkey e novo voto/upload são testes com escrita e exigem janela própria.
   Cadastrar novas enquetes pelo fluxo normal antes de abrir; sem enquetes ativas,
   o código retorna `waiting_for_polls`. Este pacote não cria enquetes.

## Falhas e recuperação

Antes de COMMIT, erro/encerramento da conexão aborta a transação. Queda durante
COMMIT tem resultado ambíguo: inspecionar o estado, não repetir cegamente (revisão
seria incrementada outra vez). Nunca chamar cleanup para "testar a conexão".

Após COMMIT, restaurar primeiro em clone e planejar recuperação seletiva das
tabelas de participação e `community_progress`, considerando FKs, XP e escritas
posteriores. Não executar `pg_restore --clean` em PRD, nem restaurar admins,
passkeys/counters ou key ring automaticamente. Avançar revisão ao republicar o
estado recuperado. Exclusão SQL não apaga WAL, backups ou páginas mortas.

## Verificações desta persistência

Apenas revisão estática e `bash -n`; nenhum backup, restore, cleanup ou ensaio
de escrita foi realizado nesta etapa. Snapshot acima veio da auditoria histórica.
