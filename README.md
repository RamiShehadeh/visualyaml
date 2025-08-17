# Visual YAML - Yaml Diff Tool for Unity

A Unity Editor extension that makes it easy to visualize differences between YAML-serialized Unity assets (such as prefabs). The tool compares two versions of YAML files and highlights changes in a user-friendly interface, including clickable asset references.

![image](Screenshots/YAML-Diff-screen.png)
![image](Screenshots/YAML-Diff-screen1.png)


## Features

- **YAML Diffing:**  
  Compare YAML files (prefabs for now) to detect added, modified, or removed fields.

- **Git Integration:**  
  Retrieve diff information from Git commits (with a manual fallback for file selection).

- **Clickable Asset Links:**  
  Automatically detect and replace GUIDs with the corresponding asset name. Clickable links will ping the asset in the Project window.

- **Custom Inspector Extension:**  
  Highlights component changes directly in the Inspector by color-coding and grouping diff information.

## Installation

1. **Clone Package**  
   Copy this repository url and import it via Unity's Package Manager: Window -> Package Manager -> + -> install package from GIT url

2. **Import YamlDotNet into your plugins folder if it is not already working (package already contains .dll for it):**  
   Place the `YamlDotNet.dll` (compatible with your Unity version) into your `Assets/Plugins` folder.


## Usage

1. **Open the Diff Tool Window:**  
   In Unity, go to **Window → YAML Prefab Diff** to open the main diff window.

2. **Fetch Changed Assets:**  
   - Click **Fetch Changed (Git Diff)** to automatically list changed YAML assets from your Git repository.
   - Alternatively, use **Manual Diff** to compare any two YAML files.

3. **View Diff Results:**  
   Clicking on an asset in the list opens a detailed diff view showing changes by YAML path, with clickable GUID links for asset references.

## Requirements

- **Unity:** 2021.3 or later (This was made in Unity 6000.0.30f1)
- **YamlDotNet:** Ensure you have a compatible version of the YamlDotNet DLL in your project. Go to Project Settings -> Player -> Make sure Api Compatibility Level is the same as the .dll you used (I used 2.1)
- **Git:** Required for Git diff functionality. Your Unity project must be a git repo for this to work.

## Contributing

Contributions, issues, and feature requests are welcome! Feel free to open an issue or submit a pull request.

