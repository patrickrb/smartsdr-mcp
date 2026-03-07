// ****************************************************************************
///*!	\file IPAddressValidator.xaml.cs
// *	\brief ValidationRule for IP Address
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-08-23
// *	\author Abed Haque, AB5ED
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Windows.Controls;
using System.Windows.Data;
using System.Reflection;

namespace Flex.UiWpfFramework.Utils
{
    public class IPAddressValidator : ValidationRule
    {
        public override ValidationResult Validate (object value, System.Globalization.CultureInfo cultureInfo)
        {

            // Get and convert the value
            string stringValue = GetBoundValue(value) as string;

            if (stringValue == null)
            {
                return new ValidationResult(false, "value cannot be empty.");
            }
            else
            {
                string ipString = stringValue.ToString();
                IPAddress ip;

                if (!HasFourQuartets(ipString))
                    return new ValidationResult(false, "4 quartets required.");
                else if (!QuartetsAreNumbers(ipString))
                    return new ValidationResult(false, "IP address only contain numbers.");
                else if (!QuartetsAreInValidRange(ipString))
                    return new ValidationResult(false, "Quartets must be between 0 and 255.");
                else if (!IPAddress.TryParse(stringValue.ToString(), out ip))
                    return new ValidationResult(false, "Not a valid IP Address.");
            }
            return ValidationResult.ValidResult;
        }

        private bool HasFourQuartets(string ip)
        {
            return (ip.Split('.').Length == 4);
        }
        
        private bool QuartetsAreNumbers(string ipString)
        {
            List<string> quartets = ipString.Split('.').ToList<string>();
            return quartets.All<string>(x => IsInt(x));
        }

        private bool QuartetsAreInValidRange(string ipString)
        {
            List<string> quartets = ipString.Split('.').ToList<string>();
            List<int> quartetsInts = quartets.Select(int.Parse).ToList();

            return quartetsInts.All<int>(x => (x >= 0 && x <=255));
            
        }

        private bool IsInt(string s)
        {
            int x = 0;
            return int.TryParse(s, out x);
        }

        //https://stackoverflow.com/questions/10342715/validationrule-with-validationstep-updatedvalue-is-called-with-bindingexpressi
        private object GetBoundValue(object value)
        {
            if (value is BindingExpression)
            {
                // ValidationStep was UpdatedValue or CommittedValue (Validate after setting)
                // Need to pull the value out of the BindingExpression.
                BindingExpression binding = (BindingExpression)value;

                // Get the bound object and name of the property
                string resolvedPropertyName = binding.GetType().GetProperty("ResolvedSourcePropertyName", BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance).GetValue(binding, null).ToString();
                object resolvedSource = binding.GetType().GetProperty("ResolvedSource", BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance).GetValue(binding, null);

                // Extract the value of the property
                object propertyValue = resolvedSource.GetType().GetProperty(resolvedPropertyName).GetValue(resolvedSource, null);


                // This is what we want.
                return propertyValue;
            }
            else
            {
                // ValidationStep was RawProposedValue or ConvertedProposedValue
                // The argument is already what we want!
                return value;
            }
        }
    }
}
