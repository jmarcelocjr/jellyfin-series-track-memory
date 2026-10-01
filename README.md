# Series Track Memory (Jellyfin 12.x)

Plugin que memoriza o áudio e a legenda escolhidos numa série, como a Netflix.
Você troca as faixas no player uma vez, em qualquer episódio. Os outros episódios da série já abrem assim, em qualquer cliente.

## Como funciona

O Jellyfin já guarda a faixa escolhida em cada episódio ("remember selections") e usa isso como padrão no próximo play.
O plugin estende isso para a série inteira.

1. Durante o play, o servidor registra a faixa escolhida. O plugin escuta esse registro e descreve a faixa pelo idioma, título e flags forced/SDH.
2. Ele procura a faixa equivalente em cada outro episódio da série, mesmo em índice diferente, e grava como seleção lembrada.
3. No próximo episódio, o próprio servidor entrega essa faixa como padrão, então não há troca visível depois do play.
4. Episódios adicionados depois recebem a preferência assim que são escaneados.

Séries que você nunca ajustou e filmes continuam com o padrão global do usuário.
Se um episódio não tem o idioma aprendido, ele usa o padrão global e o plugin não "desaprende" a preferência.

## Requisitos

- Jellyfin 12.x.
- Cada usuário precisa manter ligadas as opções de lembrar a seleção de áudio e de legenda, nas configurações de reprodução. As duas vêm ligadas por padrão. Com uma delas desligada, o plugin ignora aquela parte.

## Instalação no servidor (NAS)

### Pelo catálogo, recomendado

1. Suba este projeto para um repositório no GitHub.
2. Crie uma tag de versão com quatro números, por exemplo `v1.0.0.0`. O workflow testa, gera o zip, publica a release e atualiza o `manifest.json`.
   ```
   git tag v1.0.0.0 && git push origin v1.0.0.0
   ```
   Para versões de teste, use um sufixo, por exemplo `v0.1.0.0-alpha`. O plugin recebe a versão `0.1.0.0` e a release do GitHub sai como pré-release. O Jellyfin não aceita sufixo na versão do plugin.
3. No Jellyfin, vá em Painel > Plugins > Repositórios e adicione:
   ```
   https://raw.githubusercontent.com/jmarcelocjr/jellyfin-series-track-memory/main/manifest.json
   ```
4. Instale "Series Track Memory" no catálogo e reinicie o servidor.

### Manual

```
scripts/package.sh 0.1.0.0 jmarcelocjr/jellyfin-series-track-memory
```

Copie `artifacts/publish/Jellyfin.Plugin.SeriesTrackMemory.dll` para `<pasta de config do Jellyfin>/plugins/SeriesTrackMemory_0.1.0.0/` no NAS e reinicie o servidor.

## Uso

- Anime: no primeiro episódio, escolha áudio japonês e legenda pt-BR e deixe tocar alguns segundos.
- Desenho dublado: escolha áudio português e desligue a legenda.
- A página do plugin no Painel lista as séries aprendidas por usuário. O botão "Forget" apaga a preferência e limpa as seleções gravadas.

## Observações

- A opção do jellyfin-web "Definir faixa com base no item anterior" também escolhe faixas na reprodução contínua. Em geral as duas concordam. Se houver conflito, desligue essa opção.
- O aprendizado usa os relatórios de progresso do player, que chegam a cada poucos segundos. Uma troca feita e desfeita logo em seguida pode não ser registrada.
- O plugin registra no log do servidor linhas "Learned tracks" e "Propagated".

## Desenvolvimento

```
dotnet build
dotnet test
```
