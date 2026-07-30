using Sufficit.Finance;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Translates Efí Billing API errors into safe, actionable operator guidance.
/// Keep this provider-specific knowledge at the gateway boundary.
/// Source: https://dev.efipay.com.br/docs/erros/consulta/
/// </summary>
internal static class EfiBankSlipErrorCatalog
{
    private static readonly IReadOnlyDictionary<string, EfiBankSlipErrorDefinition> Definitions =
        new Dictionary<string, EfiBankSlipErrorDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["3500000"] = ProviderFailure(
                "A Efí não conseguiu processar a solicitação",
                "Não repita a emissão sem antes consultar a cobrança na Efí. Se não houver cobrança criada, tente novamente mais tarde; se persistir, acione o suporte da Efí."),
            ["3500001"] = Security(
                "A aplicação não possui permissão na Efí",
                "Revise as permissões da aplicação e da conta Efí. Depois de corrigir a autorização, solicite uma nova tentativa."),
            ["3500002"] = Validation(
                "Faltou um parâmetro obrigatório",
                "Confira o campo indicado no detalhe, corrija o cadastro ou a cobrança e então use “Retomar emissão”."),
            ["3500007"] = Security(
                "A emissão de boleto não está habilitada",
                "Habilite o meio de pagamento boleto na conta Efí ou confirme o contrato com o provedor antes de tentar novamente."),
            ["3500008"] = Security(
                "A Efí recusou a autenticação",
                "Revise o Client ID, o Client Secret, o ambiente e as permissões da aplicação. Não repita a emissão enquanto a autenticação não estiver válida."),
            ["3500010"] = Validation(
                "A requisição contém um campo desconhecido",
                "Revise o campo indicado no detalhe e ajuste os dados enviados antes de tentar novamente."),
            ["3500023"] = Validation(
                "Faltou uma propriedade obrigatória",
                "Preencha a propriedade indicada no detalhe, salve o cadastro e então use “Retomar emissão”."),
            ["3500025"] = Validation(
                "Uma propriedade possui valor inválido",
                "Corrija o valor indicado no detalhe e então use “Retomar emissão”."),
            ["3500030"] = Security(
                "A cobrança já possui uma forma de pagamento",
                "Consulte a cobrança existente na Efí. Não crie outra cobrança nem troque de provedor até confirmar o resultado anterior."),
            ["3500034"] = Validation(
                "A Efí recusou os dados da cobrança",
                "Revise o campo e a mensagem indicados no detalhe, corrija o cadastro e então use “Retomar emissão”."),
            ["3500036"] = Definitive(
                "A cobrança não é um boleto",
                "Confirme o identificador da cobrança na Efí. Esta cobrança não pode ser retomada como boleto."),
            ["3500037"] = Validation(
                "Uma propriedade da cobrança é inválida",
                "Corrija a propriedade indicada no detalhe e então use “Retomar emissão”."),
            ["3500038"] = Definitive(
                "O estado atual da cobrança não permite alteração",
                "Consulte o histórico da cobrança na Efí e confirme o estado atual antes de decidir por uma nova cobrança."),
            ["3500043"] = Definitive(
                "O estado atual da cobrança não permite cancelamento",
                "Consulte o histórico na Efí. Somente cobranças em estado compatível podem ser canceladas."),
            ["3500044"] = Definitive(
                "A cobrança não pode ser paga no estado atual",
                "Consulte o histórico da cobrança na Efí e confirme se ela já foi paga, cancelada ou expirou."),
            ["3500050"] = Validation(
                "A conta informada é inválida",
                "Revise a conta Efí configurada para este tenant antes de tentar novamente."),
            ["3500072"] = Security(
                "A conta Efí está impedida de emitir",
                "Confirme no painel da Efí se existe bloqueio cadastral ou operacional e acione o suporte do provedor. Não repita a emissão até liberar a conta."),
            ["3500081"] = Definitive(
                "O estado da cobrança é incompatível com a operação",
                "Consulte o histórico da cobrança e confirme seu estado atual antes de qualquer nova ação."),
            ["3500101"] = Validation(
                "A requisição contém valores inválidos",
                "Revise os campos indicados no detalhe, corrija os dados e então use “Retomar emissão”."),
            ["4600001"] = Definitive(
                "A cobrança não foi encontrada na Efí",
                "Confirme o identificador da cobrança e o ambiente configurado. Não emita outra cobrança até descartar uma divergência de ambiente."),
            ["4600002"] = Validation(
                "Um campo da cobrança é inválido",
                "Corrija o campo indicado no detalhe e então use “Retomar emissão”."),
            ["4600007"] = Validation(
                "A data de vencimento já passou",
                "Escolha uma data de vencimento igual ou posterior a hoje e então use “Retomar emissão”."),
            ["4600009"] = ProviderFailure(
                "A Efí não conseguiu gerar o link do boleto",
                "Consulte a cobrança na Efí antes de repetir. Se ela existir sem link, tente a consulta novamente ou acione o suporte do provedor."),
            ["4600037"] = Security(
                "O valor excede o limite operacional da conta Efí",
                "Revise os limites da conta no painel da Efí ou reduza o valor da cobrança. Não repita a mesma emissão até resolver o limite."),
            ["4600060"] = Validation(
                "Uma data informada é inválida",
                "Corrija a data indicada no detalhe e então use “Retomar emissão”."),
            ["4600100"] = ProviderFailure(
                "A Efí excedeu o tempo ao validar os dados",
                "Consulte primeiro a cobrança na Efí para confirmar se ela foi criada. Só tente novamente depois de descartar uma emissão anterior."),
            ["4600142"] = Validation(
                "Os dados cadastrais não conferem",
                "Confira o CPF ou CNPJ e os dados do pagador indicados no detalhe. Corrija o cadastro antes de tentar novamente."),
            ["4600209"] = Security(
                "O limite diário de emissões foi atingido",
                "Aguarde a renovação do limite ou regularize os dados exigidos pela Efí. Não use outro provedor como failover para contornar este bloqueio."),
            ["4600210"] = Security(
                "O limite de cobranças idênticas foi atingido",
                "Não tente novamente este boleto. Consulte o histórico na Efí para confirmar as cobranças já emitidas; se uma nova cobrança for realmente necessária, altere de forma consciente a descrição, o vencimento ou o valor."),
            ["4600211"] = Security(
                "O limite mensal de emissões foi atingido",
                "Revise o limite da conta Efí ou aguarde sua renovação. Não use outro provedor como failover para contornar este bloqueio."),
            ["4600222"] = Validation(
                "Recebedor e pagador não podem ser a mesma pessoa",
                "Revise o CPF ou CNPJ do pagador e do recebedor antes de tentar novamente."),
            ["4600224"] = Security(
                "A autorização da aplicação Efí é inválida",
                "Revise a autorização da aplicação, as credenciais e o ambiente. Não repita a emissão até a autenticação estar regularizada."),
            ["4600414"] = Validation(
                "O vencimento ultrapassa o limite permitido",
                "Escolha uma data de vencimento dentro do limite informado pela Efí e então use “Retomar emissão”."),
            ["4600521"] = Validation(
                "A data do desconto é inválida",
                "Ajuste a data do desconto para o período permitido e então use “Retomar emissão”."),
            ["4600523"] = Validation(
                "O valor após o desconto ficou abaixo do mínimo",
                "Reduza o desconto ou aumente o valor da cobrança e então use “Retomar emissão”."),
            ["4699999"] = ProviderFailure(
                "A Efí devolveu uma falha inesperada",
                "Consulte a cobrança na Efí antes de qualquer repetição. Se o resultado continuar inconclusivo, acione o suporte do provedor."),
            ["validation_error"] = Validation(
                "A Efí recusou os dados da cobrança",
                "Revise o detalhe técnico, corrija os dados da cobrança ou do pagador e então use “Retomar emissão”."),

            ["efi_payer_phone_invalid"] = Validation(
                "O telefone do pagador é inválido",
                "Informe um telefone brasileiro com DDD e 10 ou 11 dígitos, salve o cadastro e então use “Retomar emissão”."),
            ["efi_payer_address_missing"] = Validation(
                "O endereço do pagador não foi informado",
                "Preencha o endereço do pagador no cadastro e então use “Retomar emissão”."),
            ["efi_payer_address_street_missing"] = Validation(
                "A rua do pagador não foi informada",
                "Preencha a rua no endereço do pagador e então use “Retomar emissão”."),
            ["efi_payer_address_number_missing"] = Validation(
                "O número do endereço não foi informado",
                "Preencha o número no endereço do pagador, salve o cadastro e então use “Retomar emissão”."),
            ["efi_payer_address_incomplete"] = Validation(
                "O endereço do pagador está incompleto",
                "Preencha bairro, cidade, CEP com 8 dígitos e UF com 2 letras; salve o cadastro e então use “Retomar emissão”.")
        };

    public static EfiBankSlipErrorDefinition Resolve(
        string? code,
        string? name,
        BankSlipErrorCategory fallbackCategory,
        BankSlipOperation operation)
    {
        if (!string.IsNullOrWhiteSpace(code)
            && Definitions.TryGetValue(code.Trim(), out var definition))
        {
            return WithOperationCategory(definition, operation);
        }

        if (!string.IsNullOrWhiteSpace(name)
            && Definitions.TryGetValue(name.Trim(), out definition))
        {
            return WithOperationCategory(definition, operation);
        }

        if (operation == BankSlipOperation.Issue
            && (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(name)))
        {
            return new EfiBankSlipErrorDefinition(
                BankSlipErrorCategory.AmbiguousResult,
                "A Efí devolveu um erro ainda não classificado",
                "Consulte a cobrança e o histórico na Efí antes de qualquer repetição. Registre o código para ampliar o catálogo.");
        }

        return fallbackCategory switch
        {
            BankSlipErrorCategory.Validation => Validation(
                "A Efí recusou os dados enviados",
                "Revise o detalhe técnico, corrija os dados da cobrança ou do pagador e então use “Retomar emissão”."),
            BankSlipErrorCategory.SecurityBlock => Security(
                "A Efí bloqueou a operação por segurança",
                "Consulte a cobrança e o histórico na Efí. Não repita nem troque de provedor até entender o bloqueio."),
            BankSlipErrorCategory.DefinitiveRejection => Definitive(
                "A Efí recusou a operação",
                "Consulte o detalhe e o histórico da cobrança. Corrija a causa antes de criar uma nova cobrança."),
            BankSlipErrorCategory.Retryable => new EfiBankSlipErrorDefinition(
                BankSlipErrorCategory.Retryable,
                "A Efí informou uma falha temporária",
                "Aguarde alguns instantes e use “Retomar emissão” se a cobrança não tiver sido criada."),
            _ => ProviderFailure(
                operation == BankSlipOperation.Issue
                    ? "O resultado da emissão na Efí é inconclusivo"
                    : "A Efí não concluiu a operação",
                operation == BankSlipOperation.Issue
                    ? "Consulte a cobrança na Efí antes de qualquer repetição para evitar emissão duplicada."
                    : "Consulte o estado atual na Efí e tente novamente apenas quando o resultado anterior estiver confirmado.")
        };
    }

    private static EfiBankSlipErrorDefinition WithOperationCategory(
        EfiBankSlipErrorDefinition definition,
        BankSlipOperation operation)
        => definition.Category.HasValue
            ? definition
            : new EfiBankSlipErrorDefinition(
                operation == BankSlipOperation.Issue
                    ? BankSlipErrorCategory.AmbiguousResult
                    : BankSlipErrorCategory.ProviderUnavailable,
                definition.Title,
                definition.Action);

    private static EfiBankSlipErrorDefinition Validation(string title, string action)
        => new(BankSlipErrorCategory.Validation, title, action);

    private static EfiBankSlipErrorDefinition Security(string title, string action)
        => new(BankSlipErrorCategory.SecurityBlock, title, action);

    private static EfiBankSlipErrorDefinition Definitive(string title, string action)
        => new(BankSlipErrorCategory.DefinitiveRejection, title, action);

    private static EfiBankSlipErrorDefinition ProviderFailure(string title, string action)
        => new(null, title, action);
}

internal sealed class EfiBankSlipErrorDefinition
{
    public EfiBankSlipErrorDefinition(
        BankSlipErrorCategory? category,
        string title,
        string action)
    {
        Category = category;
        Title = title;
        Action = action;
    }

    public BankSlipErrorCategory? Category { get; }
    public string Title { get; }
    public string Action { get; }
}
