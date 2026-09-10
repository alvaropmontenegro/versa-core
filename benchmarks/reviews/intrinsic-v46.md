# Intrinsic v46 — diagnóstico aplicado e comparação controlada

Data da revisão: 2026-09-09. Os timestamps de execução são preservados conforme o relógio do ambiente.

## Mudanças

- Snapshot padrão `gpt-5.4-mini-2026-03-17`; esforço por etapa: escrita `medium`, semântica/editorial `none`. Ambos configuráveis. Temperature omitida em todas as variantes comparadas.
- Telemetria: modelo retornado, esforço solicitado, request ID, finish reason, fingerprint quando disponível, tokens de raciocínio e hashes SHA-256 de entrada e prompt/schema. Recusa, resposta vazia e término diferente de stop são falhas de etapa. Timeout por chamada: 120 segundos.
- Respostas originais preservadas em `model-responses.json` nas auditorias da compilação final. O benchmark completo inicial antecedeu apenas essa persistência adicional; sua configuração de julgamento é a mesma.
- Extração usa display calculado. Capturas novas anotam o DOM no navegador original; antigas usam renderização offline com scripts e rede desativados. Display:block separa rótulos e display:none é excluído. CSS externo ausente em capturas antigas não é recuperado. Não há garantia de visibilidade por pixels ou oclusão.
- Prompt de escrita reorganizado em interpretação, confirmação do defeito e avaliação de reparo, com exemplos contrastivos de regras gerais. `rule` e `defectConfirmed` precedem classificação/correção. Candidatos não confirmados não são convertidos em issues. A declaração do modelo continua falível; não equivale a verificação linguística independente.
- Política de gramática cobre também a categoria `grammar`, para impedir que uma concordância mal classificada gere substituição. Exceção: remoção localizada de uma palavra consecutiva duplicada verificada mecanicamente; ainda depende de o modelo reconhecer corretamente que a repetição é acidental.
- Capitalização puramente estilística também é filtrada em `ui_text`. `editValidation` deixa explícito que a verificação é de localização. `confidence` é null, com `confidenceBasis=not_calibrated`.
- Prompt de contradição explicita polaridades opostas sobre a mesma proposição. A arquitetura permanece com três chamadas independentes, sem verificador adicional.

## Método e limites causais

1. Compilação de controle: prompts e extração da v45, acrescentando somente snapshot/configuração explícitos e telemetria. Quatro capturas existentes, duas rodadas em none e duas em medium. As duas variantes omitem temperature. Hashes confirmam entradas e prompts/schemas idênticos entre esforços. Isso isola esforço entre essas variantes, mas não reproduz exatamente a v45 histórica, que usava temperature=0 e alias do modelo.
2. Configuração v46: benchmark principal de 19 casos, controles congelados em duas rodadas e cinco capturas Cylogy antigas. A melhoria conjunta não identifica a contribuição individual de cada mudança de prompt, extração e validação.
3. As 19 capturas efetivamente usadas na rodada principal foram preservadas em `benchmarks/frozen-v46/`, com expectativas inalteradas em `benchmarks/v46-frozen-full.json`, para repetição sem recaptura. A execução da repetição foi dividida em dois arquivos apenas para reduzir tempo de espera; isso não altera os casos.

## Comparação isolada de esforço com prompts v45

| Esforço em todas as etapas | Rodada 1 | Rodada 2 |
|---|---:|---:|
| none | 2/4 | 1/4 |
| medium | 3/4 | 3/4 |

A escrita usou zero tokens de raciocínio em none e 1.000/1.403 tokens somados por rodada em medium. O controle regional falhou nas duas rodadas none e passou nas duas medium. Entretanto, medium produziu title_body_mismatch no autor inline nas duas rodadas. Esse resultado sustenta experimentar medium na escrita, sem assumir benefício na análise semântica. A amostra é pequena e não demonstra superioridade universal.

Arquivos: `benchmarks/results/benchmark-v46-baseline-none.json`, `benchmarks/results/benchmark-v46-baseline-none-repeat.json`, `benchmarks/results/benchmark-v46-baseline-medium.json`, `benchmarks/results/benchmark-v46-baseline-medium-repeat.json`.

## Validação local

Build final: zero erros e warnings. 39 verificações: 29 de extração/contrato/política, 7 do comparador e 3 de recorrência. Os novos testes exercitam CSS de bloco, display:none, preservação inline, incerteza de detecção, capitalização UI e tentativa de contornar a política por categoria grammar. O harness copia o driver do Playwright, necessário para os testes de layout.

## Resultado inicial v46

Benchmark completo: **18/19**, contra os 14/19 registrados na v45. Quatro controles: **4/4 nas duas repetições**. Endesa conserva as duas correções exatas, o controle regional e o autor inline não geram escrita falsa, e o controle positivo conserva as duas correções esperadas.

Única falha do benchmark completo: Bufete Padilla. A antiga substituição `Bestill Time` não retornou; surgiu um falso positivo de concordância em:

> Its charming old town with whitewashed houses, thriving arts scene, and elegant marina attract a sophisticated international community — particularly from Scandinavia, the Netherlands, and Germany.

O modelo assume que o sujeito inteiro é apenas old town. Existe leitura coordenada de old town, arts scene e marina; não foi demonstrado erro obrigatório. O finding recebeu requires_review e nenhuma substituição, mas continua sendo um falso positivo. Não alteramos a expectativa para aceitá-lo.

## Fontes da configuração

- [Modelo e snapshot GPT-5.4-mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini)
- [Tokens de raciocínio](https://developers.openai.com/api/docs/guides/reasoning)
- [Structured Outputs e limites de correção semântica](https://developers.openai.com/api/docs/guides/structured-outputs)

## Repetição completa e capturas Cylogy

A repetição das 19 capturas terminou em **18/19**, com o mesmo falso positivo em `attract` no Bufete. Hashes de entrada e de prompt/schema foram iguais nos 19 casos. O manifesto original contém um caso vivo não pontuado com o mesmo nome de Bufete; a preparação foi corrigida para usar a definição pontuada, e esse caso foi executado separadamente e incorporado ao relatório combinado. Nenhuma expectativa foi flexibilizada.

Relatório combinado: `benchmarks/results/benchmark-v46-frozen-repeat-full.json`. O resultado demonstra repetição da aprovação/reprovação desses casos; não significa respostas idênticas, e categorias extras permitidas variaram.

Cinco capturas Cylogy: cinco auditorias concluídas, **22 evidências de escrita aceitas** (v45: 20). Não existe anotação exaustiva dessas capturas, portanto a contagem não mede precisão ou recall. Persistem achados problemáticos:

- Symposium: `team attend` reaparece como erro, apesar do sucesso do controle isolado; a explicação impõe concordância en-US, contrariando a instrução sobre variantes aceitas. Ficou para revisão, sem edição.
- Company: `90’s → 90s` reaparece como obrigação de pontuação; permanece questionável como defeito objetivo, em vez de convenção editorial.
- Symposium: `Tzikaki → Tzikakis` foi proposto por comparação com outra ocorrência do nome. Consistência não prova qual grafia está correta; a substituição permanece uma limitação da avaliação do modelo.
- Sitecore: `delivers` e `lend` continuam como duas ocorrências de uma possível inconsistência, ambas para revisão.

A v46 melhora o benchmark controlado, mas não demonstra que a revisão linguística esteja resolvida para páginas reais. O esforço medium e o novo contrato não eliminam falsos positivos dependentes do contexto. Não foram introduzidas exceções específicas para aprovar esses textos, nem um novo verificador. As 30 páginas vivas não foram reexecutadas.

## Totais e artefatos

67 auditorias, 201 chamadas, nenhuma etapa com falha técnica. Resumo verificável: `benchmarks/results/benchmark-v46-validation-summary.json`. As respostas brutas das 32 auditorias da compilação final estão arquivadas em `benchmarks/v46-model-responses/`; incluem controles, Cylogy e repetição completa. Os 19 casos da primeira rodada foram capturados com o código de julgamento final, antes apenas da persistência adicional das respostas brutas.

Configuração e correções estão implementadas; ainda há falsos positivos linguísticos. Uma nota de benchmark alta não deve ser apresentada como confiança estatística ou garantia para páginas não anotadas.
