# PPM Checker Tool

Processor Power Management (PPM) settings can significantly impact power and performance of Windows systems. These settings configure getting the right threads on the right cores at the right time and frequency that balance performance and power requirements. 
Configuring the PPM settings is a complex process and requires area expertise and understanding of each setting’s impact on the system’s behavior. There are many settings with many dimensions (Power Scheme, Power Source, Processor Class, Quality of Service etc.) which create numerous numbers of configurations and add to the complexity of the PPM configuration task.

To help this process, we built a tool that the silicon vendors/OEMs can deploy to validate the PPM settings on their new products.

> The process
> ![image](https://user-images.githubusercontent.com/121056171/210118095-0926239e-2e7f-4dbf-8015-0dfcc630b92b.png)

> The Architecture
> ![image](https://user-images.githubusercontent.com/121056171/210118102-93b0a087-0562-4b1d-baa3-0ef5aae138ce.png)


## How to use the Automatic Script
The script is located in the Github release, as well as in the Scripts folder in the PPMCheckerTool.
### To Run the script with the release
Download the release from Github. Unzip it. Run the script Run.ps1 from the Scripts folder.

‘ExeLocation’ refers to the location that both the project Exe’s reside – PPMCheckerTool.exe and SetSliderPowerMode.exe. In this case, it is 1 directory up. Enter ‘..’ in the prompt. Can be relative path.

‘OutputFilePath’ refers to the full name and path of the output file. Eg. D:\Outputs\Output.txt will work. Can be relative path.

### To Run the script with the visual studio development environment

Open the solution in Visual Studio, ensure you have no build errors. For this method to work, similar to the above method, you need to ensure that both the projects .exe is in the same folder. Or, you can modify the Run.ps1 script to take hardcoded absolute paths.

Right click on both the projects – PPMCheckerTool and SetSliderPowerMode -> Publish -> Local Folder -> Enter same local folder location.

Run the script Run.ps1

‘ExeLocation’ – Enter the local folder

‘OutputFilePath’ – Enter full name and path of the output file. Eg. D:\Outputs\Output.txt will work. Can be relative path.


## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
