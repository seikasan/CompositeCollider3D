# CompositeCollider3D

閉じた3D ColliderをBoolean演算で結合し、静的または動的な衝突形状を作れます。

![複数の立体を結合して凹形状のColliderを生成](Packages/com.seikasan.composite-collider-3d/Documentation~/composite-collider-3d.svg)

[English README](README.md)

## 導入方法

1. **Window > Package Manager**を開きます。
2. **+ > Add package from git URL**を選びます。
3. 次のURLを入力します。

   `https://github.com/seikasan/CompositeCollider3D.git?path=/Packages/com.seikasan.composite-collider-3d`

## 使用方法

1. GameObjectに`CompositeCollider3D`を追加し、**Sources**に2個以上のColliderを指定します。**Use Child Colliders**を押すと、子GameObjectのColliderを一覧へ追加できます。
2. 通常の演算は**Merge**です。**Difference**または**Intersect**を使うColliderと同じGameObjectに`CompositeColliderSource3D`を追加し、演算を選びます。
3. **Static Concave**または**Dynamic Convex**を選び、**Generate Geometry**を押します。Dynamic ConvexではRigidbodyと複数の凸MeshColliderを生成します。
4. 必要に応じてPhysics MaterialやLayer Overridesを設定します。**Save Generated Meshes...**を押すと、生成メッシュをプロジェクトのアセットとして保存できます。

Sourcesの先頭が初期形状になり、そのOperationは無視されます。2個目以降は一覧の順に演算します。たとえば`A → Difference B → Merge C`は`(A − B) ∪ C`になります。

## 対応Collider

- `MeshCollider`：読み込み可能で、閉じていて、面の向きが揃ったメッシュが必要です。
- `BoxCollider`、`SphereCollider`、`CapsuleCollider`：Boolean演算の前にポリゴンメッシュへ変換します。SphereとCapsuleは近似形状です。

Dynamic Convexは凹形状を複数の凸形状で近似します。Sceneビューで結果を確認し、実際の用途に合わせて衝突挙動をテストしてください。Static Concaveは非凸MeshColliderを1つ生成するため、非Kinematic Rigidbodyとは併用できません。

## 生成方法

**Generation Type**は**Manual**または**Automatic**を選べます。Automaticでは編集中に入力形状、演算、相対Transform、Collision Representationの変更を確認して再生成します。Play ModeとPlayerでは自動再生成しません。Inspectorからの生成はバックグラウンドで進み、新しい結果ができるまでは前回のColliderを使います。スクリプトから`GenerateGeometry()`を呼ぶ場合は同期実行です。

Dynamic Convexの**Advanced (CoACD)** では、Concavity Threshold、Sample Resolution、MCTS Iterationsを調整できます。サンプル数や反復回数を減らすと生成時間が短くなる場合があります。Thresholdを上げるとパーツ数を減らせる一方、凹部の近似が粗くなる場合があります。**Generated Info**には最後の生成結果と処理時間が表示されます。

CompositeCollider3Dを付けたGameObjectのScaleは`(1, 1, 1)`にし、入力Colliderはその子に置いてください。生成に成功すると、二重に衝突しないよう入力Colliderを無効にします。

## 対応環境

CoACDのネイティブライブラリはWindows x86_64、macOS、Linux x86_64用を同梱しています。一方、ManifoldNETのネイティブライブラリは現在Windows x86_64用のみです。そのため、Boolean生成を利用できる環境は現時点でWindows x86_64に限られます。

他の環境でもBoolean生成するには、同梱しているManifoldNET 1.0.7-alphaのC APIと互換性のある`manifoldc`を追加します。対応するソースは`weianweigan/manifold-csharp`のcommit `94bd588a03bf6c22c41c2bf2539b1c980e497b6e`です。

- macOS：arm64とx86_64両方に対応する`libmanifoldc.dylib`（Universal Binary推奨）
- Linux：x86_64用の`libmanifoldc.so`
- `manifoldc`をoneTBBなどの共有ライブラリと動的リンクした場合は、その対応ファイルも必要です。macOSでは`otool -L`、Linuxでは`ldd`で依存先を確認できます。

ファイルは`Runtime/Plugins/Manifold`以下に追加し、Unity Plugin Importerで対象プラットフォームを設定します。必要なバイナリを入手したらお知らせください。パッケージに取り込んでImporter設定を整えます。

## About

Unity向けComposite Collider 3D
