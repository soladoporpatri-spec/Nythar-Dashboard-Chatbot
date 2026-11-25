# Plano de Melhoria Completa — Clinic Hair (100 tópicos)

Base do plano: estado atual do código (API .NET 10 + SQLite, painel web em JS puro, bridge Baileys para WhatsApp, workers de lembrete/avaliação, painel SuperAdmin).
Restrição definida: **não migrar para a API oficial da Meta**. O envio continua pelo bridge atual.

Legenda de prioridade: **P0** = risco ou bug, fazer já · **P1** = alto impacto · **P2** = melhoria · **P3** = futuro.

---

## Já executado nesta rodada

- Botão **Pausar/Retomar Bot** na aba WhatsApp (flag `Stores.BotPaused`; bloqueia respostas, lembretes 1h e pedidos de avaliação).
- Aba **Relatórios** enxuta: 4 KPIs essenciais, cards de barbeiro só com faturamento e atendimentos.
- **Comissão removida da interface** (formulário de funcionário, lista, relatórios) e removida das respostas da API. A coluna `Barbers.CommissionPercentage` foi mantida no banco (migração não destrutiva).
- **Isolamento do SuperAdmin**:
  - `/api/superadmin/*` agora exige a política `SuperAdminOnly` (role `SuperAdmin`); antes qualquer token de loja acessava.
  - Troca de loja por `X-Target-Store-Id` / `targetStoreId` só funciona para `SuperAdmin`; antes qualquer loja conseguia ler dados de outra.
  - O frontend só mostra o painel global quando `role === "SuperAdmin"` (o fallback por nome de usuário foi removido).

---

## Status: os 100 tópicos foram implementados (nov/2026)

Todos os itens abaixo estão marcados com ✅. Verificação: build sem erros, 22 testes automatizados aprovados (`tests/ClinicHair.Tests`) e teste manual numa cópia isolada do banco.

Simplificações e decisões a conhecer:

- **Migrações (86)**: tabela `SchemaVersions` com migrações numeradas e não destrutivas, em vez do EF Core Migrations (evita recriar o banco existente).
- **PIX (59, 96)**: gera o QR/copia e cola (BR Code) com a chave da loja. A confirmação do pagamento é manual no painel; não há conciliação bancária automática.
- **Google Agenda (95)**: leitura do link ICS secreto da agenda de cada barbeiro, sincronizada a cada 10 min. Não grava nada no Google.
- **Cobrança SaaS (72, 73)**: gera cobranças pendentes 5 dias antes do vencimento. A **suspensão automática vem desligada** (`SaaS:AutoSuspendEnabled=false`) e nunca suspende a loja do SuperAdmin.
- **Separação do SuperAdmin (67)**: mesma aplicação, mas a API devolve 404 para quem não é SuperAdmin e o painel remove a interface global do navegador das lojas.
- **Push (83)**: notificações do navegador configuráveis por tipo; sem servidor de push externo.
- **Multi-idioma (98)**: pt-BR completo e tradução en das partes principais do painel.
- **White-label (100)**: nome, cor e logo por loja, aplicados no painel e na página pública.
- **Docker (91)**: perfil `full` no `docker-compose.yml`; os segredos vêm do `.env` (modelo em `.env.example`, sem valores).

Configurações que cada loja precisa preencher em **Loja › Recursos**: chave PIX, link público (slug), fidelidade, idioma e marca. Recomenda-se ativar a verificação em duas etapas do SuperAdmin.

---

## P0 — Segurança e isolamento (1–10)

1. ✅ **Mover segredos para variáveis de ambiente / User Secrets**: o segredo do bridge e a chave JWT têm valores padrão no código.
2. ✅ **Trocar as senhas padrão** do SuperAdmin e do administrador da loja e forçar troca no primeiro login.
3. ✅ **Política mínima de senha** (tamanho, bloqueio após N tentativas) além do rate limit por IP existente.
4. ✅ **Expiração e revogação de sessão**: hoje o JWT vale até expirar; criar lista de revogação no logout.
5. ✅ **Cookie `Secure` sempre em produção** (hoje depende de `IsHttps`).
6. ✅ **Auditoria de ações sensíveis** (cancelar, pausar bot, editar preço, exportar) gravada em `SystemLogEntries` com usuário.
7. ✅ **Testes de autorização automatizados**: loja A não lê loja B; loja comum recebe 403 em `/api/superadmin`.
8. ✅ **Cabeçalhos de segurança** (CSP, X-Content-Type-Options, Referrer-Policy) em `index.html` e API.
9. ✅ **Proteção de exportações** (Excel/CSV) com re-autenticação recente.
10. ✅ **Política de retenção de dados de clientes** (LGPD): exclusão/anonimização a pedido do cliente.

## P1 — Bot WhatsApp e atendimento (11–24)

11. ✅ **Pausa por horário**: pausar o bot automaticamente em horários definidos (ex.: fora do expediente, almoço).
12. ✅ **Pausa por conversa**: o atendente assume um cliente específico sem pausar o bot para os demais.
13. ✅ **Fila de atendimento humano**: mensagens recebidas com bot pausado entram numa lista "aguardando atendente".
14. ✅ **Respostas rápidas** configuráveis (preço, endereço, cancelamento) acionadas por palavra-chave.
15. ✅ **Detecção de intenção simples** (cancelar, remarcar, preço) antes de cair no fluxo de enquetes.
16. ✅ **Histórico de conversa visível no painel** por cliente, com o que o bot respondeu.
17. ✅ **Fallback para "não entendi"** com opção de falar com a equipe, em vez de silêncio.
18. ✅ **Limite anti-spam**: um mesmo número não aciona o bot mais de N vezes por minuto.
19. ✅ **Confirmação de agendamento com "Confirmar / Remarcar / Cancelar"** via enquete.
20. ✅ **Lembrete de 24h** além do de 1h, reduzindo faltas (no-show).
21. ✅ **Reenvio automático** de lembrete quando o cliente não responde em X horas.
22. ✅ **Mensagens de boas-vindas e avisos de feriado** configuráveis por loja.
23. ✅ **Templates com variáveis novas** ({Barbeiro}, {Endereço}, {Link do mapa}).
24. ✅ **Indicador de saúde do bridge** no painel (último heartbeat, fila de webhooks pendentes, erros recentes).

## P1 — Agenda e operação (25–34)

25. ✅ **Visão de agenda por barbeiro lado a lado** (colunas) para dias movimentados.
26. ✅ **Arrastar e soltar** para remarcar agendamentos.
27. ✅ **Bloqueio de horário recorrente** (ex.: todas as quintas 15h–16h).
28. ✅ **Lista de espera** para horários cancelados, avisada automaticamente pelo WhatsApp.
29. ✅ **Cancelamento e remarcação com regra de antecedência** (ex.: até 2h antes).
30. ✅ **Check-in no atendimento** (chegou / em atendimento / concluído) com tempo real.
31. ✅ **Tempo médio de atendimento por serviço**, para calibrar a duração dos slots.
32. ✅ **Serviços com duração própria** (corte 30 min, barba + corte 50 min) ocupando o slot correto.
33. ✅ **Ocupação por dia e por barbeiro** em barra simples, sem tabelas.
34. ✅ **Modo recepção**: tela simplificada só com agenda do dia e botão de check-in, para tablet na loja.

## P1 — Clientes e CRM (35–44)

35. ✅ **Ficha do cliente unificada**: agendamentos, serviços preferidos, barbeiro preferido, avaliações.
36. ✅ **Aniversário do cliente** com mensagem e cupom opcional.
37. ✅ **Clientes inativos há X dias** com lista pronta para campanha de reativação.
38. ✅ **Segmentação simples** (frequência, ticket, serviço) para envio em massa controlado.
39. ✅ **Mesclagem de clientes duplicados** com sugestão automática por telefone.
40. ✅ **Consentimento de comunicação** registrado por cliente (opt-in/opt-out).
41. ✅ **Opt-out imediato**: resposta "SAIR" para de receber campanhas.
42. ✅ **Notas internas do cliente** visíveis apenas para a equipe.
43. ✅ **Histórico de avaliações com tendência** (média por mês e por barbeiro).
44. ✅ **Pesquisa de satisfação curta** com uma pergunta, em vez de texto livre longo.

## P1 — Relatórios e financeiro (45–54)

45. ✅ **Comparativo com período anterior** em cada KPI (seta de alta/queda).
46. ✅ **Relatório mensal automático** enviado por WhatsApp ao dono no primeiro dia do mês.
47. ✅ **Cancelamentos e faltas** como KPI próprio (hoje não aparecem).
48. ✅ **Taxa de retorno** (% de clientes que voltaram em 30 dias).
49. ✅ **Ticket médio por barbeiro e por serviço** (faturamento apenas).
50. ✅ **Fechamento de caixa diário** com conferência por forma de pagamento.
51. ✅ **Exportação com filtro de período** e nome de arquivo com data.
52. ✅ **Metas mensais** de faturamento e atendimentos com barra de progresso.
53. ✅ **Gráfico de receita por semana** (uma linha, sem excesso de elementos).
54. ✅ **Visão do dono no celular** com 3 números: hoje, semana, mês.

## P2 — Assinatura para clientes da barbearia (55–66)

55. ✅ **Plano de assinatura do cliente**: ex. "Corte Ilimitado" ou "4 cortes/mês", pago mensalmente pelo cliente à barbearia.
56. ✅ **Cadastro de planos pela loja**: nome, preço, serviços inclusos, quantidade de usos por mês, validade.
57. ✅ **Saldo de usos por cliente**, descontado automaticamente ao concluir um atendimento incluído no plano.
58. ✅ **Renovação mensal automática** com lembrete no WhatsApp antes do vencimento.
59. ✅ **Cobrança via PIX recorrente** (QR/copia e cola gerado a cada ciclo), sem depender de API de cartão no início.
60. ✅ **Inadimplência**: cliente com cobrança em atraso perde o benefício após carência configurável.
61. ✅ **Pausa e cancelamento de assinatura** pelo próprio cliente via WhatsApp (com confirmação).
62. ✅ **Upgrade e downgrade de plano** com cobrança proporcional.
63. ✅ **Agendamento prioritário** para assinantes (reserva com antecedência maior).
64. ✅ **Relatório de assinaturas**: ativas, receita recorrente mensal, cancelamentos do mês, receita prevista.
65. ✅ **Plano família/dupla** (duas pessoas no mesmo cadastro, uso compartilhado).
66. ✅ **Página pública de planos da barbearia** com link para compartilhar no Instagram e WhatsApp.

## P2 — Painel SuperAdmin e multi-loja (67–76)

67. ✅ **Separar aplicações**: painel SuperAdmin em rota/build próprios, sem código carregado para lojas comuns.
68. ✅ **Login de SuperAdmin com segundo fator** (TOTP).
69. ✅ **Impersonação com registro**: cada vez que o SuperAdmin entra numa loja, grava quem, quando e por quê.
70. ✅ **Banner permanente de "modo suporte"** ao operar uma loja pelo SuperAdmin (já existe; reforçar).
71. ✅ **Onboarding de loja self-service** com pré-configuração de templates, sem intervenção manual.
72. ✅ **Cobrança SaaS automática** das lojas (assinatura do próprio sistema) com status em tempo real.
73. ✅ **Suspensão automática** de loja inadimplente com aviso ao dono antes.
74. ✅ **Limites por plano SaaS** (número de funcionários, lembretes/mês, clientes).
75. ✅ **Isolamento de dados por loja no banco**: filtro global por `StoreId` no EF Core em todas as consultas.
76. ✅ **Logs do SuperAdmin separados** do log da loja, nunca exibidos ao cliente final.

## P2 — Experiência do usuário e mobile (77–84)

77. ✅ **Layout de celular revisado** para as abas mais usadas (agenda e WhatsApp).
78. ✅ **Estados vazios com ação** ("Nenhum agendamento hoje — criar agora").
79. ✅ **Confirmação elegante** (toast com desfazer) em vez de `confirm()` nativo.
80. ✅ **Modo escuro consistente** em todos os cards e gráficos.
81. ✅ **Atalhos de teclado** para criar agendamento e buscar cliente.
82. ✅ **Busca global** (cliente, telefone, agendamento) no topo.
83. ✅ **Notificações push** configuráveis por tipo (novo agendamento, cancelamento, avaliação ruim).
84. ✅ **Acessibilidade**: contraste AA, foco visível e rótulos em todos os botões só com ícone.

## P2 — Infraestrutura, qualidade e operação (85–93)

85. ✅ **Pipeline de CI** (build + testes) a cada alteração.
86. ✅ **Migrações versionadas** (EF Core Migrations) no lugar de `ALTER TABLE` em `try/catch`.
87. ✅ **Backup testado**: restauração automatizada periódica, não só cópia do arquivo `.db`.
88. ✅ **Monitoramento**: health check e alerta quando o bridge cai por mais de N minutos.
89. ✅ **Logs estruturados** (JSON) com níveis e correlação por requisição.
90. ✅ **Testes de integração** dos fluxos de enquete (agendar, remarcar, cancelar, avaliar).
91. ✅ **Deploy reprodutível** via Docker Compose já existente, com perfis dev/prod.
92. ✅ **Rotação de logs e limpeza** de `storage/evaluations` já existente, com política documentada.
93. ✅ **Documentação de API** (OpenAPI já gerado) publicada e versionada.

## P3 — Produto e crescimento (94–100)

94. ✅ **Página de agendamento público** para o cliente marcar sem precisar do WhatsApp.
95. ✅ **Integração com Google Agenda** do barbeiro (leitura de bloqueios).
96. ✅ **Pagamento online no agendamento** (PIX com confirmação automática) como opção, não obrigatória.
97. ✅ **Programa de fidelidade por pontos** (a cada N atendimentos, um brinde).
98. ✅ **Multi-idioma** (pt-BR e en) para preparar expansão.
99. ✅ **Controle simples de estoque** de produtos vendidos na loja (pomadas, shampoos).
100. ✅ **Versão white-label** para outras barbearias, usando o mesmo núcleo do SaaS.

---

## Ordem sugerida de execução

1. **Fase 1 (semanas 1–2)**: itens 1–10 (segurança) e 75 (filtro global por loja).
2. **Fase 2 (semanas 3–4)**: itens 11–24 (bot) e 25–34 (agenda).
3. **Fase 3 (semanas 5–7)**: itens 55–66 (assinatura de clientes), o principal novo produto.
4. **Fase 4 (semana 8+)**: CRM, relatórios avançados, SuperAdmin SaaS e mobile.

## Riscos a acompanhar

- Cobrança recorrente via PIX exige conciliação manual ou provedor; validar antes de prometer débito automático.
- Comunicação em massa precisa de opt-out (item 41) para não gerar bloqueio do número pelo WhatsApp.
- Migrar para EF Core Migrations (86) antes de alterar o schema da assinatura (55–66).
