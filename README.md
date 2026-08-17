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
- `TAVILY_API_KEY` (necessária para o fact-check da pipeline secundária)
- Opcionalmente, `OPENAI_BASE_URL`, `VERSA_DIAGNOSIS_MODEL` e `VERSA_FACT_CHECK_MODEL`

## Saída

Cada execução cria uma pasta em `bin/<configuration>/<target-framework>/output/<timestamp-slug>` com:

- `result.json`
- `captured-body.txt`
- `structural-outline.md`
- `final-clean.txt`
- `llm-debug.json`

O benchmark atual está em `benchmark.json` e pode ser executado com:

```powershell
.\run-benchmark.ps1
```

Para executar o novo desenho (entendimento único + `claimsToVerify` + fact-check):

```powershell
.\run-benchmark.ps1 -SecondaryPipeline
```
