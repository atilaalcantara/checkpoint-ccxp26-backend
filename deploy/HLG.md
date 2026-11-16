# CI/CD da API HLG

## Contrato

- `.github/workflows/hlg.yml`: somente push em `homologacao`, testes com PostgreSQL 17, build nativo ARM64, publicação GHCR e deploy pelo environment `homologation`.
- Imagens: `ghcr.io/atilaalcantara/checkpoint-api:hlg-<SHA completo>` e `:hlg`. O deploy aceita somente a tag SHA HLG; nunca usa `latest` ou tags PRD. Cache isolado `hlg-api`.
- Concurrency não cancela deploy em andamento; API e web continuam releases/workflows separados no mesmo environment. Lock global `/run/lock/checkpoint-hlg-deploy.lock` serializa API, web, rollback e ativação no host. O dispatcher aguarda até 600s usando relógio monotônico e polling de 250ms; ao expirar falha explicitamente antes de guards/mutações. O descritor mantém o lock até terminar a operação (inclusive compensação), sem apagar/substituir o arquivo de lock. O job de deploy tem limite de 20 minutos, incluindo build, espera e execução.
- SSH usa a chave ed25519 já pinada em PRD, `StrictHostKeyChecking=yes`, host fixo `oracle.atilaalcantara.com`, sem `ssh-keyscan` em runtime.
- Environments criados via `gh`: `homologation` com branch policy `homologacao`; `production` com policy `main`, ambos sem aprovação obrigatória. O job PRD usa `environment: production`; após SSH, ambos os workflows verificam externamente `/api/v1/site-status` pelo proxy TLS com `curl --fail`, retries e timeouts. O check externo falha o job, mas não dispara rollback remoto automático.

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
Admin__EnablePublicPasskeyRegistration=true
DataProtection__KeyRingPath=/var/lib/checkpoint-data-protection
Cors__Origins__0=https://ccxp26-hlg.atilaalcantara.com
Fido2__ServerDomain=ccxp26-hlg.atilaalcantara.com
Fido2__Origins__0=https://ccxp26-hlg.atilaalcantara.com
Proxy__TrustForwardedHeaders=true
```

O banco `checkpoint_hlg` e role `checkpoint_hlg_app` devem ser exclusivos de HLG. Essa role precisa dos privilégios de migração em HLG e **nenhum** acesso ao banco PRD. O volume precisa permitir escrita pelo `APP_UID` da imagem .NET. O proxy externo deve encaminhar o domínio HLG para `checkpoint-hlg-web`, mantendo o Host e terminando TLS.

O dispatcher instalado aceita env-file simples `CHAVE=valor`, comentários em linhas próprias e linhas vazias; rejeita CRLF, duplicatas (incluindo variações de caixa), aliases de conexão e chaves de conexão desconhecidas. A connection string aceita os campos mostrados acima e, opcionalmente, `Pooling`, `SSL Mode`, `Trust Server Certificate`. Host, banco, role e search path têm exatamente os valores acima; porta 5432 e Application Name `checkpoint-hlg` quando presentes. Não usar `export`, aspas externas, espaços ao redor de `=` ou senha com `;`. Não colocar secrets no GitHub vars, logs ou repositório.

## Migração e rollback

O workflow chama somente `deploy-api IMAGE`, sem enviar shell/stdin. A conta `checkpoint-hlg-deploy` tem forced command `/usr/local/sbin/checkpoint-hlg-ssh` → sudo allowlist `/usr/local/sbin/checkpoint-hlg-deploy` → `/usr/local/lib/checkpoint-hlg/operations.py`, todos root-owned. **`deploy/operations.py` é a fonte versionada do script real instalado**, incluindo guards, migrations, lock e rollback dos dois componentes. Alterações exigem instalação administrativa; checkout/CI não atualiza esse arquivo automaticamente.

`deploy/hlg.sh` agora é apenas um cliente SSH local do forced command: `bash deploy/hlg.sh deploy-api IMAGE`, `status` ou `rollback-api`. Usa chave/host key previamente configurados em `~/.ssh/config`/agente e known_hosts, com verificação estrita. Não é script para executar no servidor, não abre locks próprios nem interpreta `.env`. O CI continua usando SSH diretamente. A implementação Bash legada foi substituída para eliminar guards divergentes de Search Path/pool e caminhos de locks inexistentes no fluxo real.

Instalação administrativa: validar o candidato e testes, comparar com o instalado e, sob `flock -w 600 /run/lock/checkpoint-hlg-deploy.lock`, instalar em arquivo temporário root:root 755 no mesmo diretório e renomear atomicamente para `/usr/local/lib/checkpoint-hlg/operations.py`. Preservar o inode do lock; não reiniciar contêineres/proxy para atualizar o script.

`deploy-api` valida tag SHA, permissões, rede, volume, marcador HLG, ambos os ambientes .NET, keyring, Fido2/CORS, banco, host, role, schema e MigrateOnStartup. Faz pull e executa a imagem **NOVA** em `checkpoint-hlg-api-migrate` com `--migrate-only`, env HLG e volume HLG, antes de parar a API atual. Falha na migração mantém a API atual. O temporário é removido com `--rm`. API e migrador executam como 1654:1654, sem portas publicadas.

O contêiner anterior fica parado e desconectado da rede como `checkpoint-hlg-api-previous`, preservando imagem, env e mounts. Falha ao iniciar, na readiness `/health/ready` ou no smoke do web HLG existente restaura automaticamente esse contêiner e encerra o workflow com erro. Primeiro deploy sem versão anterior remove o candidato com falha. No próximo deploy só é retida uma versão anterior. Não há prune de imagens/volumes.

Rollback individual pela mesma conta restrita:

```bash
ssh -i "$KEY" checkpoint-hlg-deploy@oracle.atilaalcantara.com rollback-api
```

Isso restaura a imagem/configuração anterior, **não** desfaz migrations nem dados. Migrations precisam ser compatíveis com a versão anterior (expand/contract). A conta não pertence a docker/sudo; somente o wrapper validado é autorizado via sudo. Allowlist SSH exata: `status`, `deploy-api API_IMAGE`, `deploy-web WEB_IMAGE`, `rollback-api`, `rollback-web`. Não aceita `bash`, `migrate`, o antigo `deploy API WEB`, flags extras ou comandos por stdin. O frontend HLG usa `resolver 127.0.0.11 valid=10s` e `proxy_pass` variável para acompanhar a troca de IP da API. O dispatcher espera o DNS/smoke e não recarrega nem reinicia `nginx-proxy`/PRD.

## Validação

Deploy real confirmado em 2026-11-16: [backend 43920021425](https://github.com/atilaalcantara/checkpoint-ccxp26-backend/actions/runs/43920021425), tentativa **2**, sucesso; frontend [43920019861](https://github.com/atilaalcantara/checkpoint-ccxp26-frontend/actions/runs/43920019861), tentativa **2**, sucesso. A tentativa 1 esbarrou no lock global não bloqueante: concurrency por repositório não serializa API e web entre repositórios. Proxy TLS HLG já está ativo; não há ativação pendente.

Correção bounded instalada em 2026-11-16, root:root 755. SHA-256 do `operations.py` instalado e versionado: `7357cf46d911d8592ecb30d5b2ca7541ab1c7fb5470e414ce1292cdb3c9d1eb4`. Diff contra a versão anterior limitado ao docstring e aquisição de lock. Sem novo deploy/pull/migration para validar essa correção.

Regressão versionada: `python3 -B -m unittest discover -s deploy -p 'test_operations.py' -v` (11 testes): contenção real de descritores API/web, liberação, timeout bounded, erro de I/O sem retry, nenhuma mutação após timeout, migração nova antes de stop, falha de migração/start/DNS smoke, rollback individual, isolamento dos componentes e guards com Search Path/pool reais rejeitando PRD/aliases/duplicatas. Docker é simulado nos testes transacionais; nenhum auth/test existente foi alterado. O workflow HLG executa essa suíte antes dos testes .NET existentes com PostgreSQL.

Validação no host: timeout curto do helper instalado, forced command `status` bloqueado por lock mantido e concluído após liberação, guard real (sem imprimir `.env`), readiness API e smoke nginx/same-origin web. Checks externos: HLG e PRD `/api/v1/site-status` e `/`. Sintaxe Bash, `actionlint` e `git diff --check` complementam a validação. Os novos passos CI serão executados após commit/push pelo coordenador; os runs de sucesso acima antecedem esta correção.

Resultados adicionais: os quatro checks externos retornaram HTTP 200. Harness temporário dos clientes Bash aprovou 22 cenários válidos/inválidos com SSH simulado, verificando destino, host key estrita, imagem SHA e rejeição de argumentos extras/comandos arbitrários. Suíte .NET/PostgreSQL não foi repetida nesta correção de infraestrutura; seu sucesso real pertence ao run acima.

Aviso não bloqueante dos runners na tentativa 2 (backend e frontend): `docker/build-push-action@v6`, `docker/login-action@v3` e `docker/setup-buildx-action@v3` declaram Node 20, mas o runner força execução em Node 24. Jobs concluíram com sucesso. Atualização dessas actions fica como manutenção separada; não é a causa da contenção de lock.
