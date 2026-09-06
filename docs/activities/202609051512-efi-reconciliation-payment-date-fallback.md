# Fallback da data de pagamento na reconciliação EFI

## Objetivo

Corrigir a reconciliação para preencher a data de pagamento dos boletos pagos quando a resposta da EFI fornece o momento de recebimento pelo banco, evitando que a coluna permaneça vazia apesar do status `paid`.

## Estado inicial

- O inventário EFI lia somente `payment.paid_at`.
- A lista de cobranças pode retornar `status: paid` com `payment.method`, mas sem os campos temporais de pagamento.
- O fluxo de notificações já usava `received_by_bank_at` como evidência temporal do pagamento.
- O contrato compartilhado e a tela já separavam as datas da EFI e do banco local, mas registros históricos locais pagos podem não ter `PaidAtUtc` persistido.
- A documentação oficial da EFI apresenta `received_by_bank_at`, `paid_at` e `paid_value` dentro de `payment` no retorno de cobranças.

## Alterações por área

### Gateway EFI

- Criado o leitor compartilhado `ReadEfiPaymentDateUtc`.
- `payment.paid_at` permanece como fonte preferencial.
- `payment.received_by_bank_at` é usado como fallback quando `paid_at` está ausente ou inválido.
- O parser do inventário também aceita `paid_value` no objeto da cobrança como fallback.
- O parser de detalhes do boleto usa a mesma regra antes de consultar o histórico.
- O inventário agora consulta o detalhe apenas para cobranças `paid`/`settled` que ainda não possuem data; o histórico do detalhe fornece o evento `Pagamento efetuado` quando a lista não traz a data.

## Etapas da investigação da API

1. A rota `GET /v1/charges?charge_type=billet&begin_date=...&end_date=...` foi identificada como a origem da lista dos últimos dias.
2. A lista foi comparada com o contrato documentado da EFI: `data[].status` informa o estado, enquanto `data[].payment.paid_at` e `data[].payment.received_by_bank_at` podem conter a data.
3. O retorno detalhado `GET /v1/charge/{id}` foi conferido para o caso da imagem: ele pode trazer `data.payment.method`, `data.paid_value` e a data somente em `data.history[].created_at`, no evento `Pagamento efetuado`.
4. No sistema, esses dados atravessam `EfiGateway` como `ProviderBankSlipInventoryItem.PaidAtUtc`, são comparados em `BankSlipConsistencyService` e chegam à tela como `ProviderPaidAtUtc`; a data do sistema vem de `BankSlipStorageModel.PaidAtUtc`.

### Blazor

- A coluna `Pagamento` continua exibindo `EFI` e `Local` separadamente.
- Datas ausentes agora aparecem como `Não informado`, deixando claro que o sistema não possui o fato temporal, em vez de sugerir que a data seja zero ou desconhecida por falha visual.

### Testes

- O teste do inventário confirma `paid_at` em ISO-8601.
- Novo teste confirma o fallback de `received_by_bank_at` no formato civil da EFI e sua conversão para UTC.
- Novo teste reproduz o formato da resposta detalhada de produção, com `payment.method`, `paid_value` na raiz e data no histórico.
- O teste da página confirma a apresentação explícita de datas ausentes.

## Conferência de sete dias

O relatório deve ser solicitado pela rota autenticada `GET /Finance/BankSlip/Reconciliation` com `provider=efi`, `fromDate` e `toDate` cobrindo exatamente os sete dias desejados. Para cada item pago, a conferência deve verificar:

1. `ProviderStatus`/status da EFI indica `paid` ou `settled`.
2. `ProviderPaidAtUtc` foi obtido da lista, de `received_by_bank_at` ou do detalhe/histórico da cobrança.
3. `LocalPaidAtUtc` só é exibido quando existe no registro local.
4. A diferença entre as datas é observada sem alterar o boleto nem o banco durante a consulta.

Exemplo para os sete dias encerrados em 05/09/2026:

```text
/Finance/BankSlip/Reconciliation?provider=efi&fromDate=2026-08-30&toDate=2026-09-05&maximumItems=5000
```

Essa chamada exige a sessão administrativa do Blazor e credenciais EFI configuradas; os testes automatizados usam respostas reais documentadas pela EFI, mas não substituem a chamada autenticada do tenant.

## Decisões e limitações

- Não foi usado `CreatedAtUtc`, `UpdatedAtUtc` ou a data da EFI para preencher silenciosamente `LocalPaidAtUtc`; isso produziria uma informação falsa para registros locais antigos.
- A data local só será exibida quando tiver sido persistida pelo fluxo local de pagamento. Para dados antigos sem esse histórico, a tela informa `Não informado`.
- Não houve alteração de banco, commit, push ou deploy.

## Validação

- `dotnet test tests/Sufficit.Gateway.Efi.Tests.csproj --configuration Debug --no-restore -v minimal`: 32 testes aprovados.
- `dotnet test tests/Sufficit.Gateway.Efi.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~EfiGatewayInventoryTests -v minimal`: 7 testes aprovados.
- `dotnet test tests/Sufficit.Blazor.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~EfiGatewayPageTests -v minimal`: 14 testes aprovados.
- `dotnet build /mnt/sufficit/sufficit-base/src/Sufficit.Base.csproj --configuration Debug --no-restore -v minimal`: aprovado, 0 erros e 0 warnings.
- Detector visual do Impeccable nos arquivos da página: nenhum problema detectado.
- `git diff --check` nos repositórios EFI e Blazor: aprovado.
- Aplicação local em `https://localhost:26508`: health check HTTP 200.

## Referências entregues

- `/mnt/sufficit/sufficit-gateway-efi/src/EfiGateway.Inventory.cs`
- `/mnt/sufficit/sufficit-gateway-efi/src/EfiGateway.BankSlips.cs`
- `/mnt/sufficit/sufficit-gateway-efi/tests/EfiGatewayInventoryTests.cs`
- `/mnt/sufficit/sufficit-blazor/src/Features/Gateway/Efi/Pages/EfiReconciliationPage.razor`
- `/mnt/sufficit/sufficit-blazor/src/Features/Gateway/Efi/Pages/EfiReconciliationPage.razor.cs`
