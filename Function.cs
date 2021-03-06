using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace SendConfirmation
{
    public class Function
    {

        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly RegionEndpoint sqsRegion = RegionEndpoint.EUWest1;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String taskToken;
        private static String cxmEndPoint;
        private static String cxmAPIKey;
        private static String bucketName;
        private static String sqsEmailURL;

        private Secrets secrets = null;

        private static IAmazonS3 s3Client;

        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                cxmEndPoint = secrets.cxmEndPointTest;
                cxmAPIKey = secrets.cxmAPIKeyTest;
                bucketName = secrets.templateBucketTest;
                JObject jsonText = JObject.Parse(input.ToString());
                caseReference = (String)jsonText.SelectToken("CaseReference");
                taskToken = (String)jsonText.SelectToken("TaskToken");
                sqsEmailURL = secrets.sqsEmailURLTest;
                try 
                {
                    if (context.InvokedFunctionArn.ToLower().Contains("prod"))
                    {
                        cxmEndPoint = secrets.cxmEndPointLive;
                        cxmAPIKey = secrets.cxmAPIKeyLive;
                        bucketName = secrets.templateBucketLive;
                        sqsEmailURL = secrets.sqsEmailURLLive;
                    }
                }
                catch (Exception) { }
               
                CaseDetails caseDetails = await GetCaseDetailsAsync();
                String emailBody = await GetEmailBody(caseDetails);
                if(!caseDetails.updatedCase&&!caseDetails.email.Contains("northampton.gov.uk")&&!caseDetails.email.Contains("nph.org.uk")&&!caseDetails.email.Contains("northamptonpartnershiphomes"))
                {
                    if (await SendEmail(caseDetails, emailBody))
                    {
                        Console.WriteLine(caseReference + " : Sending confirmation");
                        await SendSuccessAsync();
                    }
                    else
                    {
                        Console.WriteLine(caseReference + " : Sending confirmation");
                        await SendFailureAsync("Sending email  for " + caseReference," ");
                    }
                }
                else
                {
                    Console.WriteLine(caseReference + " : Confirmation Suppressed");
                    await SendSuccessAsync();
                }
               
            }
        }

        private async Task<String> GetEmailBody(CaseDetails caseDetails)
        {
            String emailBody=null;
            Random rand = new Random();
            String emailFromName;
            if (rand.Next(0, 2) == 0)
            {
                emailFromName = secrets.botPersona1;
            }
            else
            {
                emailFromName = secrets.botPersona2;
            }
            try
            {
                s3Client = new AmazonS3Client(primaryRegion);
                GetObjectRequest objectRequest = new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = "email-no-faq.txt"
                };
                using (GetObjectResponse objectResponse = await s3Client.GetObjectAsync(objectRequest))
                using (Stream responseStream = objectResponse.ResponseStream)
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    emailBody = reader.ReadToEnd();
                }
                emailBody = emailBody.Replace("AAA", caseReference);
                emailBody = emailBody.Replace("DDD", caseDetails.name);
                emailBody = emailBody.Replace("FFF", caseDetails.enquiryDetails);
                emailBody = emailBody.Replace("NNN", emailFromName);
                emailBody = emailBody.Replace("EEE", cxmEndPoint + "/q/case/" + caseReference + "/timeline");
            }
            catch (AmazonS3Exception error)
            {
                Console.WriteLine("ERROR : Reading Email : '{0}' when reading faq template", error.Message);
                Console.WriteLine(error.StackTrace);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : An Unknown error encountered : '{0}' when reading faq template", error.Message);
                Console.WriteLine(error.StackTrace);
            }
            return emailBody;
        }

        private async Task<CaseDetails> GetCaseDetailsAsync()
        {
            CaseDetails caseDetails = new CaseDetails();
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {
                    HttpContent responseContent = response.Content;
                    String responseString = responseContent.ReadAsStringAsync().Result;
                    JObject caseSearch = JObject.Parse(responseString);
                    caseDetails.email = (String)caseSearch.SelectToken("values.email");
                    caseDetails.name = (String)caseSearch.SelectToken("values.first_name") + " " + (String)caseSearch.SelectToken("values.surname");
                    caseDetails.enquiryDetails = (String)caseSearch.SelectToken("values.enquiry_details");
                    try
                    {
                        caseDetails.updatedCase = (Boolean)caseSearch.SelectToken("values.customer_has_updated");
                    }
                    catch(Exception)
                    {
                        caseDetails.updatedCase = false;
                    }
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference, error.Message);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<Boolean> SendEmail(CaseDetails caseDetails, String emailBody)
        {
            try
            {
                Console.WriteLine("DEBUG : SQS URL is " + sqsEmailURL);

                AmazonSQSClient amazonSQSClient = new AmazonSQSClient(sqsRegion);
                try
                {
                    SendMessageRequest sendMessageRequest = new SendMessageRequest();
                    sendMessageRequest.QueueUrl = sqsEmailURL;
                    sendMessageRequest.MessageBody = emailBody;
                    Dictionary<string, MessageAttributeValue> MessageAttributes = new Dictionary<string, MessageAttributeValue>();
                    MessageAttributeValue messageTypeAttribute1 = new MessageAttributeValue();
                    messageTypeAttribute1.DataType = "String";
                    messageTypeAttribute1.StringValue = caseDetails.name;
                    MessageAttributes.Add("Name", messageTypeAttribute1);
                    MessageAttributeValue messageTypeAttribute2 = new MessageAttributeValue();
                    messageTypeAttribute2.DataType = "String";
                    messageTypeAttribute2.StringValue = caseDetails.email;
                    MessageAttributes.Add("To", messageTypeAttribute2);
                    MessageAttributeValue messageTypeAttribute3 = new MessageAttributeValue();
                    messageTypeAttribute3.DataType = "String";
                    messageTypeAttribute3.StringValue = secrets.organisationName + " : Your Call Number is " + caseReference;
                    MessageAttributes.Add("Subject", messageTypeAttribute3);
                    sendMessageRequest.MessageAttributes = MessageAttributes;
                    SendMessageResponse sendMessageResponse = await amazonSQSClient.SendMessageAsync(sendMessageRequest);
                    return true;
                }
                catch (Exception error)
                {
                    Console.WriteLine("ERROR : Error sending SQS message : '{0}'", error.Message);
                    Console.WriteLine(error.StackTrace);
                    await SendFailureAsync("Sending confirmation to SQS", error.Message);
                    return false;
                }
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : Error starting AmazonSQSClient : '{0}'", error.Message);
                Console.WriteLine(error.StackTrace);
                await SendFailureAsync("Starting AmazonSQSClient", error.Message);
                return false;
            }
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }
    }

    public class CaseDetails
    {
        public String email { get; set; } = "";
        public String name { get; set; } = "";
        public String enquiryDetails { get; set; } = "";
        public Boolean updatedCase { get; set; } = false;
    }

    public class Secrets
    {
        public String cxmEndPointTest { get; set; }
        public String cxmEndPointLive { get; set; }
        public String cxmAPIKeyTest { get; set; }
        public String cxmAPIKeyLive { get; set; }
        public String sqsEmailURLLive { get; set; }
        public String sqsEmailURLTest { get; set; }
        public String botPersona1 { get; set; }
        public String botPersona2 { get; set; }
        public String organisationName { get; set; }
        public String norbertSendFromLive { get; set; }
        public String norbertSendFromTest { get; set; }
        public String templateBucketTest { get; set; }
        public String templateBucketLive { get; set; }
    }
}
