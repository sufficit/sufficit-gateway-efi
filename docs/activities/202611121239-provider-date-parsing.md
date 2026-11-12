# Fuso explícito e fallback GMT−3 nas datas dos provedores

Escopo: corrigir a pendência documentada anteriormente. Instantes com `Z`/offset
mantêm o fuso informado; sem fuso, interpretar em UTC−03:00 fixo, conforme decisão
expressa do mantenedor. Não reescrever histórico financeiro nem alterar vencimentos.

## Conferência da documentação Asaas

Consultados em 12/11/2026:

- [Recepção de webhooks](https://docs.asaas.com/docs/receba-eventos-do-asaas-no-seu-endpoint-de-webhook)
- [Eventos de checkout](https://docs.asaas.com/docs/checkout-events)
- [Eventos de notas fiscais](https://docs.asaas.com/docs/webhook-para-notas-fiscais)

Esses guias apresentam `dateCreated` sem offset nos exemplos. Não foi encontrada
neles uma declaração global de GMT−3 para toda a API. O fallback fixo é o contrato
estabelecido pelo mantenedor, não uma citação atribuída ao fornecedor.

## Implementação e regressões

Asaas: parser privado único para webhooks de pagamentos (também usado na consulta
de boletos), Pix, checkout e notas fiscais. Valores sem fuso usam GMT−3 antes da
normalização em UTC; datas inválidas/ausentes permanecem desconhecidas pelo parser.
Datas civis `DateOnly` ficam fora da conversão. Preservados os fallbacks preexistentes
de relógio para eventos cuja data não esteja disponível.

Efí: o leitor usa GMT−3 fixo nas datas sem fuso, inclusive históricas, removendo
aplicação implícita de horário de verão. Offsets explícitos com/sem dois-pontos ou
apenas horas são reconhecidos. A confirmação bancária na descrição, entregue
anteriormente, foi preservada.

Asaas antes: quatro falhas e quatro passes na regressão; Efí antes: uma falha e
seis passes, com a diferença histórica de 2018 reproduzida. Após correção:

- 88 testes Asaas, inclusive execução em `TZ=Pacific/Honolulu`;
- 52 testes Efí;
- 88 Asaas e 52 Efí contra cada par de DLLs candidatas (API e Background);
- 116 testes de autorização de clientes, permissões, checkout e voz contra a API
  preservada e as dependências candidatas;
- comparação de superfícies públicas dos quatro candidatos sem membros removidos
  ou adicionados; versões-base mantidas (API 1.26.1112.1427; Background 1.26.1109.30).

Somente `Sufficit.Gateway.Asaas.dll` e `Sufficit.Gateway.Efi.dll` mudam em cada
aplicação. Preservados Base, EFData, Standard, executáveis, runtimes nativos,
configuração e demais dependências. Nenhuma correção retrospectiva de recebimentos
ou lançamento financeiro foi executada nesta tarefa.

## Entrega

Publicados os gateways na API (eveo-apps, castrum-apps e apoint-apps), com
status Healthy e todos os 173 hashes de DLLs conferidos em cada instância.
Background em eveo-apps com live healthy e os 93 hashes conferidos. Publicação
pelos deploy.py oficiais; preservados runtimes nativos e configurações existentes.
No Background, o source_folder do artefato foi informado em memória à entrada
normal do script, mantendo políticas e configuração versionada. O fluxo manual
de Apoint recebeu a confirmação da publicação autorizada pelo mantenedor.

Checkpoints concluídos: documentação oficial e regressões; implementação e testes;
publicação com conferência; documentação e diff verificados para commit/push nas
mains. Este registro substitui o plano de execução da tarefa.

Evidências detalhadas e logs privados:
`/mnt/workspaces/sufficit/tmp/provider-date-parsing-20261112/`.
Comentários inline e READMEs atualizados; a documentação anterior continua como
registro histórico da pendência, agora resolvida. Nenhuma worktree criada. Os
artefatos próprios de publicação são removidos após confirmação de instalação.
