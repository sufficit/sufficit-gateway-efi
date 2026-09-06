# Atividade: corrigir a origem da data de pagamento da Efí

## Objetivo

Eliminar a inconsistência observada na reconciliação de boletos, usando somente
o campo oficial de pagamento da API Efí e evitando que timestamps de eventos
diferentes sejam apresentados como data de pagamento.

## Estado inicial

Na tela de reconciliação, uma cobrança marcada como `paid` apresentava datas
diferentes entre Efí e o registro local. O gateway preenchia a data pela ordem
`payment.paid_at`, `paid_at` no objeto raiz, `received_by_bank_at` e, em alguns
casos, pelo último `history[].created_at`.

## Causa

Esses campos não são equivalentes. A resposta de `GET /v1/charges` documenta a
data de pagamento em `data[].payment.paid_at`; a consulta detalhada usa o mesmo
campo em `data.payment.paid_at`. `received_by_bank_at` e eventos do histórico
não devem substituir essa evidência na reconciliação.

Referências: [status das transações Efí](https://dev.efipay.com.br/docs/api-cobrancas/status/),
[lista de cobranças Efí](https://dev.efipay.com.br/docs/api-cobrancas/boleto/) e
[notificações Efí](https://dev.efipay.com.br/docs/api-cobrancas/notificacoes/).

## Alterações

- `EfiGateway.Inventory` lê somente `payment.paid_at` na lista.
- Quando a lista não contém esse campo para uma cobrança paga, a consulta
  detalhada pode buscar novamente somente `data.payment.paid_at`.
- Ausência do campo permanece como `null` e gera resultado parcial informado;
  nenhum timestamp alternativo é promovido silenciosamente.
- `EfiGateway.BankSlips` deixou de usar `paid_at` no objeto raiz e
  `history[].created_at` como data de pagamento.
- O parser de notificações permanece usando explicitamente
  `received_by_bank_at`, que é o campo próprio do contrato do endpoint de
  notificações; esse valor não é usado como fallback na reconciliação.
- Os testes agora cobrem `payment.paid_at`, ausência de `paid_at`, rejeição de
  `received_by_bank_at` e rejeição do histórico.

## Validação

- `dotnet test tests/Sufficit.Gateway.Efi.Tests.csproj --no-restore -v minimal`:
  32 testes passaram, 0 warnings.
- `dotnet build src/Sufficit.Gateway.Efi.csproj --no-restore -v minimal`:
  4 projetos, 0 erros e 0 warnings.
- `dotnet build /mnt/sufficit/sufficit-endpoints/src/Sufficit.EndPoints.csproj
  --no-restore -v minimal`: não concluído por erro preexistente em
  `sufficit-ai/runtime/Connectors/Groq/GroqOpenAICompatibleModelCatalogExtension.cs:59`
  (`Contains` sem sobrecarga compatível), fora do escopo desta alteração.

## Escopo de entrega

Nenhum deploy foi realizado. O working tree já continha alterações anteriores
em `.gitignore`, `README.md` e documentação; elas foram preservadas.
