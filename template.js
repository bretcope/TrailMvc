"use strict";

var Fs = require('fs');
var Path = require('path');

function run()
{
	if (process.argv.length < 4)
	{
		console.error('Usage: node template.js <ServiceProviderType> <ServiceProviderPropertyName> [OutputFile]');
		process.exit(1);
	}
	
	var input = Path.resolve(__dirname, 'src/TrailMvc/TrailMvc.cs');
	var file = Fs.readFileSync(input, 'utf8');
	var typeName = process.argv[2];
	var propName = process.argv[3];
	var output = process.argv.length > 4 ? process.argv[4] : 'TrailMvc.generated.cs';
	output = Path.resolve(process.cwd(), output);
	
	if (Fs.statSync(output).isDirectory())
		output = Path.join(output, 'TrailMvc.generated.cs');
	
	file = file.replace(/ICustomServiceProvider|ServiceProviderProperty/g, function (match)
	{
		if (match === 'ICustomServiceProvider')
			return typeName;
		
		if (match === 'ServiceProviderProperty')
			return propName;
	});
	
	Fs.writeFileSync(output, file, 'utf8');
	console.log('Trail MVC generation complete. File saved at ' + output);
	console.log();
}

run();
