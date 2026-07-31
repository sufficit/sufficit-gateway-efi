# Sufficit Gateway Efí

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
        "Credentials": {}
      }
    }
  }
}
```

Client ID e Client Secret não pertencem a este repositório nem ao payload das
filas. O host resolve referências opacas por `IGatewayCredentialResolver` a
partir da configuração protegida.

## Validação

```bash
dotnet test tests/Sufficit.Gateway.Efi.Tests.csproj
```

Os testes usam um `HttpMessageHandler` controlado e não acessam contas reais.
