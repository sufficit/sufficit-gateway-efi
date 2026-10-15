# Alinhamento de dependências para publicar a correção de transferências

A árvore runtime da API inclui este gateway. O checkout tinha um pin local 10.0.12 → 10.0.13. Antes de integrá-lo, o build do HEAD commitado em worktree isolada foi executado: falhou em restore com quatro NU1605 (Logging.Abstractions, Options.ConfigurationExtensions, DependencyInjection.Abstractions e Configuration.Abstractions), pois as dependências atuais exigem 10.0.13. O alinhamento é necessário para compilar a publicação autorizada, sem alteração de comportamento do gateway. Validar build net10.0 antes de integrar este único arquivo.

Autorização da sessão: usuário solicitou “merge e deploy” das correções financeiras e suas dependências necessárias. Sem mensagem a terceiros ou alteração de configuração do gateway.
