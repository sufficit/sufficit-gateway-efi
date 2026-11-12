# Sufficit Gateway Efí

> **Worktrees (padrão Sufficit):** toda árvore de trabalho deste projeto (humanos ou agentes de IA) deve ser criada dentro da pasta do próprio projeto: `git worktree add .worktrees/<nome>`. A pasta `.worktrees/` é ignorada pelo git (`.gitignore` → `**/.worktrees/`) e nunca deve ser versionada ou criada fora da raiz do repositório.


Integração HTTP tipada da Sufficit com as APIs Efí.

`EfiGateway` é a fachada geral do provedor. Cada produto é implementado como
uma capacidade parcial; a primeira capacidade implementa `IBankSlipGateway` e
`IBankSlipProviderDiagnosticsGateway` para o provider persistido `efi`, sem
referenciar nem alterar o SDK ou o gateway legado da Gerencianet.

## Responsabilidades

- compartilhar autenticação, cliente HTTP, configuração e credenciais entre
  as capacidades Efí;
- emitir, consultar e cancelar boletos;
- reconciliar resultados ambíguos antes de permitir uma nova emissão;
- normalizar estados, erros e orientações operacionais;
- oferecer consultas tipadas e somente leitura para a console de diagnóstico.

## Fluxo de boleto

1. OAuth2 `client_credentials` em `POST /v1/authorize`;
2. criação da cobrança em `POST /v1/charge`;
3. associação do boleto em `POST /v1/charge/{charge_id}/pay`;
4. consulta em `GET /v1/charge/{charge_id}`;
5. cancelamento em `PUT /v1/charge/{charge_id}/cancel`.

O catálogo `EfiBankSlipErrorCatalog` traduz códigos oficiais para categorias
internas e orientação segura ao operador. Não há failover automático.

Erros de segurança, limites de emissão e respostas ambíguas bloqueiam retry
automático. Códigos ainda desconhecidos durante a emissão são tratados como
resultado ambíguo até que uma consulta descarte a criação da cobrança.

## Configuração

O host registra o gateway e a infraestrutura neutra separadamente:

```csharp
services.AddSufficitGatewayInfrastructure(configuration);
services.AddSufficitBankSlipInfrastructure(configuration);
services.AddSufficitGatewayEfi(configuration);
```

As opções e credenciais do provedor ficam em `Sufficit:Gateway:Efi`. Nenhuma
configuração geral do Efí pertence ao módulo de boletos:

```json
{
  "Sufficit": {
    "Gateway": {
      "Efi": {
        "BillingSandboxBaseAddress": "https://cobrancas-h.api.efipay.com.br/",
        "BillingProductionBaseAddress": "https://cobrancas.api.efipay.com.br/",
        "Timeout": "00:00:30",
        "TokenClockSkew": "00:00:30",
        "IncludePayerEmail": false,
        "_IncludePayerEmail": "Quando true, envia o e-mail do pagador à Efí. A Efí poderá disparar mensagens de cobrança diretamente; mantenha false quando a comunicação for feita pela Sufficit.",
        "Credentials": {}
      }
    }
  }
}
```

`IncludePayerEmail` é um opt-in. Quando a requisição de emissão informa essa
decisão explicitamente, ela prevalece sobre o padrão do gateway. No fluxo V2,
o valor vem das configurações Efí do tenant e nasce desligado. O campo `_IncludePayerEmail`
é apenas documentação inline do arquivo JSON e é ignorado pelo binder.

Client ID e Client Secret não pertencem a este repositório nem ao payload das
filas. O host resolve referências opacas por `IGatewayCredentialResolver` a
partir da configuração protegida.

## Validação

```bash
dotnet test tests/Sufficit.Gateway.Efi.Tests.csproj
```

Os testes usam um `HttpMessageHandler` controlado e não acessam contas reais.

## Recebimento e confirmação

Nas notificações, `received_by_bank_at` representa o recebimento bancário;
`created_at` da transição paga alimenta `PaymentConfirmedAtUtc`, separadamente.
Estados não pagos não atribuem confirmação. Na consulta detalhada,
`payment.paid_at` já representa essa confirmação. Nenhuma data é substituída
pelo horário de captura ou processamento interno. A projeção financeira pode
mostrar a confirmação no texto quando seu dia brasileiro difere do recebimento.

## Regra de fuso das datas do provedor

Decisão do mantenedor em 12/11/2026: Efí e Asaas são provedores brasileiros.
Datas/horários recebidos **sem fuso explícito** devem ser interpretados como
**GMT−3 (UTC−03:00, Brasília)**, independentemente do fuso do servidor.

- Respeitar `Z` ou qualquer offset explicitamente informado; não aplicar −03:00
  novamente a um instante que já possui fuso.
- Para normalizar um horário sem fuso, assumir −03:00 antes de converter para UTC.
  Exemplo: `2026-11-03 07:00:00` corresponde a `2026-11-03T10:00:00Z`.
- Quando uma data de recebimento sem hora precisar representar um instante,
  a meia-noite de Brasília corresponde a `03:00:00Z` do mesmo dia.
- Datas puramente civis, como vencimentos `DateOnly`, continuam datas civis;
  não deslocar seu dia mediante conversão de fuso.
- Relógios de captura/processamento não substituem recebimento ou confirmação.
  O padrão GMT−3 é fixo; não presumir outro offset para dados históricos sem
  evidência explícita do provedor.

Estado da implementação: valores sem fuso usam UTC−03:00 fixo, inclusive em
datas históricas, sem aplicar horário de verão. Valores com `Z` ou offset explícito
mantêm seu instante, inclusive offsets positivos e sem dois-pontos.

Registro: [atividade de documentação](docs/activities/202611121221-provider-date-timezone.md).

Pendência corrigida: [validação e publicação dos leitores de datas](docs/activities/202611121239-provider-date-parsing.md).
