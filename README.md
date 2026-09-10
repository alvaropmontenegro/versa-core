# versa-core

Console isolado para validar o pipeline de captura, extração, limpeza e análise LLM.

## Uso

```powershell
dotnet run --project C:\Dev\versa\versa-core -- "https://www.indigo.co.in/about"
```

Opcional:

```powershell
dotnet run --project C:\Dev\versa\versa-core -- "https://www.indigo.co.in/about" en
```

## Configuração

Configure as variáveis de ambiente; segredos não são armazenados no repositório:

- `OPENAI_API_KEY`
- `TAVILY_API_KEY` (necessária apenas para a pipeline de fact-check)
- Opcionalmente, `OPENAI_BASE_URL`, `VERSA_INTRINSIC_AUDIT_MODEL` e `VERSA_FACT_CHECK_MODEL`

## Saída

Cada execução cria uma pasta em `bin/<configuration>/<target-framework>/output/<timestamp-slug>` com:

- `result.json`
- `captured-body.txt`
- `structural-outline.md`
- `final-clean.txt`
- `llm-debug.json`

O benchmark da auditoria intrínseca está em `benchmarks/intrinsic.json` e pode ser executado com:

```powershell
.\benchmarks\run-benchmark.ps1
```

O relatório inclui `issueDetails`, `rejectedIssues` e `taxonomyVersion` para revisar as evidências, não apenas os códigos encontrados.
Casos podem declarar `expectedWritingCorrections` (`quote` e `correctedQuote`). Com `exactWritingCorrections: true`, correções adicionais também fazem o caso falhar. Essas expectativas pertencem ao benchmark, não às regras de idioma da pipeline.

Na versão `intrinsic-v43-contextual-evidence`, cada ocorrência de escrita possui sua própria explicação e correção. A agregação mantém uma issue por código, preservando erros distintos na mesma frase. `blockId` identifica o bloco no outline daquela captura; não representa uma posição visual precisa no site.

Para executar a pipeline de fact-check:

```powershell
.\benchmarks\run-benchmark.ps1 -Pipeline fact-check
```


### Intrinsic v47: evidência de escrita e candidatos não confirmados

A auditoria distingue a confirmação do defeito (`defectConfirmed`) da possibilidade de reparo (`correctionStatus`). Candidatos não confirmados não geram issues. Gramática e concordância ficam para revisão humana, exceto remoção de palavra duplicada verificada mecanicamente. A edição localizada em `edit` usa índices UTF-16 relativos à citação. `safe` identifica uma proposta localizada, não uma garantia linguística; `editValidation` explicita esse limite. A classificação não publica probabilidades artificiais: `confidence` é null e `confidenceBasis` é `not_calibrated`.

`writingCandidates` mantém itens não confirmados, sem efeito na classificação e sem correções. `writingAssessment` informa se existem candidatos. Cada item identifica a base da regra, a validade do original sem modificações (`originalHasValidReading`) e, para concordância, sujeito e verbo literais. Uma análise declarada ambígua, uma convenção editorial ou mera variação de nome não confirma erro. O modelo ainda pode errar a análise: os campos não são prova linguística independente. Uma página healthy pode conter candidatos não confirmados; isso significa ausência de defeitos confirmados, não garantia de texto correto.

O modelo padrão é o snapshot `gpt-5.4-mini-2026-03-17`. `VERSA_INTRINSIC_AUDIT_MODEL` permite substituí-lo. `VERSA_INTRINSIC_WRITING_REASONING_EFFORT` controla a escrita (padrão `medium`); `VERSA_INTRINSIC_REASONING_EFFORT` controla as demais inspeções (padrão `none`). As chamadas omitem temperature. Os relatórios registram o modelo retornado, esforço solicitado, tokens de raciocínio, motivo de término, request ID e hashes da entrada e prompt/schema. Respostas brutas são preservadas em `model-responses.json` no diretório da auditoria.

Capturas novas preservam o display calculado no navegador em atributos `data-versa-*`. Capturas antigas são reconstruídas offline com scripts e rede desativados: apenas CSS embutido está disponível, sem recuperar folhas externas. Essa informação separa componentes com display de bloco e exclui display:none; não comprova visibilidade por pixels, posição ou oclusão.

A IntrinsicAuditPipeline executa três chamadas independentes em paralelo: entendimento semântico, qualidade editorial (placeholders e escrita não profissional) e uma inspeção exclusiva de writing_errors. O verificador sequencial da v44 foi removido. O código reúne as evidências e exclui erros de escrita em blocos identificados como placeholders. Uma etapa com falha torna a auditoria incompleta. O escopo e as exclusões do extractor são publicados em `auditScope`; não há retrieval externo.

Execute `./benchmarks/run-local-checks.ps1` para as regressões sem LLM. `benchmarks/run-benchmark.ps1` aceita `-AdditionalCasesPath benchmarks/writing-controls.json` e, opcionalmente, `-AssemblyPath` para avaliar uma compilação específica. O comparador valida ocorrências individualmente, incluindo spans, duplicatas e evidências proibidas.

Para reanalisar uma captura preservando URL e metadados: `dotnet run --no-build -- --pipeline intrinsic --snapshot <result.json> en`. O relatório de recorrência é produzido por `benchmarks/summarize-writing-findings.ps1 -ResultsPath <benchmark.json> -OutputPath <report.json>`; recorrência não comprova identidade de componente e não muda o veredito das páginas. Veja `benchmarks/reviews/intrinsic-v47.md` para o experimento de contexto, regressão intermediária e resultados da configuração atual. As versões anteriores permanecem documentadas nos relatórios históricos.

O experimento de escrita isolada pode ser repetido com `./benchmarks/run-writing-context.ps1 -AssemblyPath <versa-core.dll> -CasesPath benchmarks/writing-context-v46.json -ResultsPath <resultado.json> -Rounds 3`. Ele usa o método de chamada, prompt e schema da compilação indicada, sem executar as outras duas inspeções. Os metadados são fixos entre frase, parágrafo e página.
