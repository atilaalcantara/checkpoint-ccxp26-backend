# CI/CD da API HLG

## Contrato

- `.github/workflows/hlg.yml`: somente push em `homologacao`, testes com PostgreSQL 17, build nativo ARM64, publicação GHCR e deploy pelo environment `homologation`.
- Imagens: `ghcr.io/atilaalcantara/checkpoint-api:hlg-<SHA completo>` e `:hlg`. O deploy aceita somente a tag SHA HLG; nunca usa `latest` ou tags PRD. Cache isolado `hlg-api`.
- Concurrency não cancela deploy em andamento; lock global `/run/lock/checkpoint-hlg-deploy.lock` serializa API, web, rollback e ativação no host.
- SSH usa a chave ed25519 já pinada em PRD, `StrictHostKeyChecking=yes`, host fixo `oracle.atilaalcantara.com`, sem `ssh-keyscan` em runtime.
- Environments criados via `gh`: `homologation` com branch policy `homologacao`; `production` com policy `main`, ambos sem aprovação obrigatória. O job PRD recebeu apenas `environment: production`, mantendo seus triggers e comandos.

## GitHub esperado (não provisionado por este código)

No environment **homologation** deste repositório:

| Tipo | Nome | Uso |
| --- | --- | --- |
| Secret | `HLG_DEPLOY_SSH_KEY` | Chave do usuário SSH restrito provisionado pelo coordenador |
| Variable | `HLG_SSH_USER` | Login restrito, obrigatório, sem fallback para o usuário PRD |

`GITHUB_TOKEN` é automático, com `packages: write` somente no job de publicação. Para pacote privado, o host precisa de credencial GHCR read-only previamente instalada pelo coordenador; o workflow não envia token do GitHub para o host.

## Infra e arquivo de ambiente

Infra provisionada: rede `checkpoint-hlg-net`, volume `checkpoint-hlg-data-protection`, PostgreSQL `personal-postgres` acessível nessa rede e diretório `/home/ubuntu/apps/checkpoint-hlg` root-owned, modo 750. A conta SSH não escreve scripts, locks ou configuração. Não há portas publicadas pelo contêiner da API.

Arquivo existente **somente no servidor**, root:root 600, `/home/ubuntu/apps/checkpoint-hlg/.env`. Contrato (senha representada por placeholder):

```dotenv
CHECKPOINT_DEPLOY_TARGET=homologation
ASPNETCORE_ENVIRONMENT=Homologation
DOTNET_ENVIRONMENT=Homologation
ConnectionStrings__Checkpoint=Host=personal-postgres;Port=5432;Database=checkpoint_hlg;Username=checkpoint_hlg_app;Password=SUBSTITUIR_NO_SERVIDOR;Search Path=checkpoint;Maximum Pool Size=5;Minimum Pool Size=0;Timeout=5;Command Timeout=15;Application Name=checkpoint-hlg
Database__MigrateOnStartup=false
Database__SeedDemo=false
Admin__EnableDevLogin=false
DataProtection__KeyRingPath=/var/lib/checkpoint-data-protection
Cors__Origins__0=https://ccxp26-hlg.atilaalcantara.com
Fido2__ServerDomain=ccxp26-hlg.atilaalcantara.com
Fido2__Origins__0=https://ccxp26-hlg.atilaalcantara.com
Proxy__TrustForwardedHeaders=true
```

O banco `checkpoint_hlg` e role `checkpoint_hlg_app` devem ser exclusivos de HLG. Essa role precisa dos privilégios de migração em HLG e **nenhum** acesso ao banco PRD. O volume precisa permitir escrita pelo `APP_UID` da imagem .NET. O proxy externo deve encaminhar o domínio HLG para `checkpoint-hlg-web`, mantendo o Host e terminando TLS.

O dispatcher instalado aceita env-file simples `CHAVE=valor`, comentários em linhas próprias e linhas vazias; rejeita CRLF, duplicatas (incluindo variações de caixa), aliases de conexão e chaves de conexão desconhecidas. A connection string aceita os campos mostrados acima e, opcionalmente, `Pooling`, `SSL Mode`, `Trust Server Certificate`. Host, banco, role e search path têm exatamente os valores acima; porta 5432 e Application Name `checkpoint-hlg` quando presentes. Não usar `export`, aspas externas, espaços ao redor de `=` ou senha com `;`. Não colocar secrets no GitHub vars, logs ou repositório.

## Migração e rollback

O workflow chama somente `deploy-api IMAGE`, sem enviar shell/stdin. A conta `checkpoint-hlg-deploy` tem forced command `/usr/local/sbin/checkpoint-hlg-ssh` → sudo allowlist `/usr/local/sbin/checkpoint-hlg-deploy` → `/usr/local/lib/checkpoint-hlg/operations.py`, todos root-owned. Esse código é a adaptação revisada de `deploy/hlg.sh`; a cópia Bash no repositório é referência, não é executada pelo CI remoto. Alterações futuras no contrato exigem revisão e instalação pelo administrador.

`deploy-api` valida tag SHA, permissões, rede, volume, marcador HLG, ambos os ambientes .NET, keyring, Fido2/CORS, banco, host, role, schema e MigrateOnStartup. Faz pull e executa a imagem **NOVA** em `checkpoint-hlg-api-migrate` com `--migrate-only`, env HLG e volume HLG, antes de parar a API atual. Falha na migração mantém a API atual. O temporário é removido com `--rm`. API e migrador executam como 1654:1654, sem portas publicadas.

O contêiner anterior fica parado e desconectado da rede como `checkpoint-hlg-api-previous`, preservando imagem, env e mounts. Falha ao iniciar, na readiness `/health/ready` ou no smoke do web HLG existente restaura automaticamente esse contêiner e encerra o workflow com erro. Primeiro deploy sem versão anterior remove o candidato com falha. No próximo deploy só é retida uma versão anterior. Não há prune de imagens/volumes.

Rollback individual pela mesma conta restrita:

```bash
ssh -i "$KEY" checkpoint-hlg-deploy@oracle.atilaalcantara.com rollback-api
```

Isso restaura a imagem/configuração anterior, **não** desfaz migrations nem dados. Migrations precisam ser compatíveis com a versão anterior (expand/contract). A conta não pertence a docker/sudo; somente o wrapper validado é autorizado via sudo. Allowlist SSH exata: `status`, `deploy-api API_IMAGE`, `deploy-web WEB_IMAGE`, `rollback-api`, `rollback-web`. Não aceita `bash`, `migrate`, o antigo `deploy API WEB`, flags extras ou comandos por stdin. O frontend HLG usa `resolver 127.0.0.11 valid=10s` e `proxy_pass` variável para acompanhar a troca de IP da API. O dispatcher espera o DNS/smoke e não recarrega nem reinicia `nginx-proxy`/PRD.

## Validação

Contrato root-owned instalado e guard real aprovado em 2026-11-16. Status SSH aprovado, 20 comandos negativos rejeitados, forwarding negado, stdin descartado e rollbacks sem versão anterior rejeitados sem mutação. Harness da adaptação root-owned: 15 testes aprovados, incluindo falha de migração antes da troca, falhas de start/readiness/DNS smoke, bootstrap e rollback por componente. Nenhum deploy/pull/migration executado no host nesta validação; fluxo positivo real será validado no primeiro deploy autorizado.

CI: testes completos com PostgreSQL, build ARM64 e readiness `/health/ready`. Local: `bash -n deploy/hlg.sh`, `git diff --check`. Sem Docker daemon local, build/runtime/migração reais são verificados pelo runner/host, não inferidos a partir de mocks. Smoke externo após provisionamento: domínio HLG e `/api/v1/site-status`, conferindo que os recursos usados são exclusivamente HLG.

Validação desta entrega (2026-11-16): `actionlint` aprovado para os workflows HLG e PRD alterado; `dotnet build Checkpoint.slnx -c Release` aprovado sem avisos/erros; sintaxe Bash e diff aprovados. Harness temporário com Docker simulado aprovou 22 cenários conjuntos API/web: deploy, falha de start/healthcheck/smoke, primeiro deploy com falha, rollback manual, falha de migração e rejeição de ambiente/banco/role PRD, aliases e duplicatas. Não foi executada a suíte backend com PostgreSQL nem build/deploy Docker real: daemon local indisponível. Environments e branch policies foram relidos pela API GitHub e confirmados.
