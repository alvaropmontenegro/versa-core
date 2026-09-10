# Intrinsic v47 — contexto e evidência de escrita

## Pergunta e desenho do experimento

Os mesmos dois textos problemáticos (Symposium: `team attend`; Bufete: `attract`) foram apresentados como frase completa, parágrafo completo e página completa, com três repetições por condição. URL, título, título visível e idioma declarado foram mantidos idênticos, inclusive na condição de frase. Apenas pageContent muda. A ordem das 18 chamadas é embaralhada com semente fixa. Não houve recaptura, retrieval ou chamadas editoriais/semânticas neste experimento.

Modelo e configuração: `gpt-5.4-mini-2026-03-17`, reasoning_effort=medium, sem temperature, com o prompt e schema de escrita efetivamente compilados na pipeline. Inputs preservados em `benchmarks/writing-context-v46.json`; runner reproduzível: `benchmarks/run-writing-context.ps1`. As respostas e os hashes são preservados no JSON de cada rodada.

## Controle com a v46

Ocorrências do falso positivo específico / três repetições:

| Texto | Frase | Parágrafo | Página |
|---|---:|---:|---:|
| Symposium — team attend | 3/3 | 2/3 | 0/3 |
| Bufete — attract | 3/3 | 1/3 | 0/3 |

Arquivo: `benchmarks/results/benchmark-v47-context-baseline.json`.

A hipótese de que reduzir o contexto resolveria esses dois casos não se confirmou. As frases isoladas foram piores nesta amostra. Como a v46 já produziu os mesmos falsos positivos em páginas completas em outras rodadas, o 0/3 desta rodada não demonstra estabilidade universal. As chamadas são probabilísticas; três repetições não permitem separar definitivamente todos os efeitos de contexto e variação entre execuções. Mantivemos a página completa na produção.

## Mudança de contrato

A classificação considera somente defeitos confirmados. `writingCandidates` preserva itens não confirmados, com `affectsClassification=false`, nenhuma correção e status de validação literal da citação. `writingAssessment` explicita a existência de candidatos sem confundi-los com falha de execução. Uma página sem defeitos confirmados pode permanecer healthy mesmo contendo candidatos; healthy não é garantia de ausência de erros.

Cada item distingue:

- evidenceBasis: regra linguística obrigatória, convenção editorial, consistência de nome ou incerteza;
- originalHasValidReading / originalReading: validade do texto ORIGINAL, sem edição;
- agreementAnalysis: sujeito completo e verbo literais, com leitura singular, plural, coletiva ou ambígua;
- defectConfirmed: conclusão sobre existência do defeito;
- correctionStatus / correctedQuote: possibilidade de reparo, separada da conclusão anterior.

O código recusa confirmação baseada em convenção/nome, leitura válida declarada ou análise ambígua de concordância. Não valida a verdade linguística da análise: o modelo ainda pode fornecer uma interpretação errada com campos estruturalmente consistentes. A política anterior de revisão de gramática/concordância permanece. Nenhuma categoria inteira foi desativada.

## Regressão intermediária identificada

O primeiro contrato usava o campo textual acceptedAlternative. O modelo preencheu esse campo com CORREÇÕES em vez de interpretações do original. O filtro então rebaixou erros reais como Oprimizely e a superior level quality, tornando Optimizely e Company healthy. Essa versão não constitui uma melhoria.

Os arquivos `benchmarks/results/benchmark-v47-context-final.json`, `benchmarks/results/benchmark-v47-full-0.json`, `benchmarks/results/benchmark-v47-full-1.json`, `benchmarks/results/benchmark-v47-controls-repeat.json` e `benchmarks/results/benchmark-cylogy-v47-frozen.json` preservam essa execução INTERMEDIÁRIA, apesar do nome histórico final no primeiro arquivo. O nome não significa aprovação da configuração.

Ela produziu 18/19 no benchmark, mas perdeu erros reais nas capturas Cylogy. Na matriz de contexto, o Symposium melhorou (0/3 nas três condições), enquanto o Bufete teve 2/3 nas três condições. Isso demonstra por que não basta observar a nota global.

O contrato foi corrigido para a pergunta booleana explícita originalHasValidReading, com explicação separada e instrução de que uma frase editada nunca é uma leitura válida do original. Os arquivos com revised representam essa configuração posterior, que está no código atual.

## Resultado da configuração revisada (código atual)

| Texto | Frase: controle → revisada | Parágrafo: controle → revisada | Página: controle → revisada |
|---|---:|---:|---:|
| Symposium — team attend | 3/3 → 0/3 | 2/3 → 0/3 | 0/3 → 0/3 |
| Bufete — attract | 3/3 → 3/3 | 1/3 → 2/3 | 0/3 → 1/3 |

São falsos positivos específicos aceitos pelo filtro de confirmação, não contagem de todos os problemas da página. Hashes da entrada foram iguais entre variantes para cada condição. O prompt/schema mudou deliberadamente. O total passou de 9/18 para 6/18, mas essa soma esconde que houve melhora no Symposium e piora no Bufete. Não demonstra melhora geral de precisão.

Arquivo: `benchmarks/results/benchmark-v47-context-revised.json`. O Symposium não apresentou o falso positivo nas nove chamadas revisadas; o Bufete continua errado nas três frases isoladas e em parte das chamadas com mais contexto. A interpretação sintática da enumeração continua sendo um gargalo; o contrato não torna a análise do modelo verdadeira.

Benchmark completo revisado: **18/19**. A falha foi Bufete: falso writing_errors em attract e omissão de language_mismatch na inspeção semântica. A repetição dos quatro controles terminou em **2/4**: Endesa e autor inline falharam. No autor houve title_body_mismatch; na Endesa a etapa editorial omitiu o placeholder e a escrita passou a corrigir palavras dentro do filler. A exclusão atual de blocos de placeholder depende de a etapa editorial reconhecê-los, portanto essa omissão também afeta a escrita. Os prompts semântico/editorial não foram alterados nesta v47, mas isso não torna as falhas irrelevantes nem prova independência entre os julgamentos na saída consolidada.

As duas correções esperadas da Endesa e as duas do controle positivo continuaram presentes, inclusive na repetição. Isso não faz a Endesa passar: as correções indevidas do filler e a ausência do placeholder continuam sendo falhas. O controle regional passou e o autor inline não recebeu writing_errors.

A revisão do campo originalHasValidReading recuperou os erros reais rebaixados pela versão intermediária: **10/10 exemplos conhecidos monitorados** foram detectados no conjunto completo + Cylogy. Incluem concordância em Each facilitator are, definately, developmer, duplicação in in, os dois acentos da Endesa, you users, Oprimizely, a superior level quality e are more. Esse conjunto não é uma anotação exaustiva das páginas e não mede recall global. O comparador original de correções permaneceu inalterado.

Nas cinco capturas Cylogy, o modelo voltou a reportar erros reais em Optimizely e Company; não retornaram nessa rodada team attend, a correção do nome Tzikaki nem 90’s → 90s. Entretanto, persistem sugestões questionáveis de pontuação e construções gramaticais. Não houve demonstração de precisão suficiente para edição automática.

## Validação e artefatos

- Build sem erros ou warnings; **46 testes locais passaram** (36 da pipeline, 7 do comparador e 3 de recorrência).
- **54 chamadas de escrita** na matriz: controle, contrato intermediário e contrato revisado; 18 por variante.
- **56 auditorias completas**: 28 por variante de contrato (19 casos + 4 controles repetidos + 5 Cylogy).
- Total: **222 chamadas**, nenhuma falha técnica de etapa.
- `benchmarks/results/benchmark-v47-validation-summary.json`: contagens, matriz e exemplos conhecidos monitorados.
- `benchmarks/results/benchmark-v47-revised-full.json`: 19 resultados consolidados da configuração atual.
- `benchmarks/results/benchmark-v47-revised-controls-repeat.json`: repetição dos quatro controles.
- `benchmarks/results/benchmark-cylogy-v47-revised-frozen.json`: cinco capturas Cylogy.
- `benchmarks/v47-model-responses/`: respostas brutas das auditorias completas; as matrizes já incluem suas respostas brutas.

A v47 está implementada e documentada como resultado experimental misto. Não deve ser apresentada como estabilização global ou sucessora comprovadamente superior à v46. A separação de candidatos é verificável no contrato, mas depende da qualidade do julgamento do modelo. O próximo experimento justificável é comparar a mesma tarefa de interpretação sintática com outro modelo, preservando texto, critérios e casos positivos; nenhuma troca de modelo foi feita nesta etapa.
