using Sitecore.Analytics;
using Sitecore.Analytics.Model.Entities;
using Sitecore.Analytics.Tracking;
using Sitecore.Commerce.Engine.Connect.Entities;
using Sitecore.Commerce.Entities;
using Sitecore.Commerce.Entities.Carts;
using Sitecore.Commerce.Entities.Customers;
using Sitecore.Commerce.Services;
using Sitecore.Commerce.Services.Customers;
using Sitecore.Commerce.XA.Foundation.Common.Context;
using Sitecore.Commerce.XA.Foundation.Common.ExtensionMethods;
using Sitecore.Commerce.XA.Foundation.Common.Models;
using Sitecore.Commerce.XA.Foundation.Common.Utils;
using Sitecore.Commerce.XA.Foundation.Connect;
using Sitecore.Commerce.XA.Foundation.Connect.Managers;
using Sitecore.Commerce.XA.Foundation.Connect.Providers;
using Sitecore.Diagnostics;
using Sitecore.Security.Authentication;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Security;

namespace Sitecore.Support.Commerce.XA.Foundation.Connect.Managers
{
    public class AccountManager : IAccountManager
    {
        public IModelProvider ModelProvider
        {
            get;
            set;
        }

        public IStorefrontContext StorefrontContext
        {
            get;
            set;
        }

        public ICartManager CartManager
        {
            get;
            set;
        }

        public CustomerServiceProvider CustomerServiceProvider
        {
            get;
            set;
        }

        public AccountManager(IConnectServiceProvider connectServiceProvider, ICartManager cartManager, IStorefrontContext storefrontContext, IModelProvider modelProvider)
        {
            Assert.ArgumentNotNull(connectServiceProvider, "connectServiceProvider");
            Assert.ArgumentNotNull(cartManager, "cartManager");
            Assert.ArgumentNotNull(storefrontContext, "storefrontContext");
            Assert.ArgumentNotNull(modelProvider, "modelProvider");
            CustomerServiceProvider = connectServiceProvider.GetCustomerServiceProvider();
            CartManager = cartManager;
            StorefrontContext = storefrontContext;
            ModelProvider = modelProvider;
        }

        public virtual ManagerResponse<GetUserResult, CommerceUser> GetUser(string userName)
        {
            Assert.ArgumentNotNullOrEmpty(userName, "userName");
            GetUserRequest request = new GetUserRequest(userName);
            GetUserResult user = CustomerServiceProvider.GetUser(request);
            if (!user.Success || user.CommerceUser == null)
            {
                string systemMessage = StorefrontContext.GetSystemMessage("User Not Found Error");
                user.SystemMessages.Add(new SystemMessage
                {
                    Message = systemMessage
                });
            }
            GetUserResult getUserResult = user;
            return new ManagerResponse<GetUserResult, CommerceUser>(getUserResult, getUserResult.CommerceUser);
        }

        public virtual bool Login(IStorefrontContext storefront, IVisitorContext visitorContext, string userName, string password, bool persistent)
        {
            Assert.ArgumentNotNullOrEmpty(userName, "userName");
            Assert.ArgumentNotNullOrEmpty(password, "password");
            string customerId = visitorContext.CustomerId;
            bool num = AuthenticationManager.Login(userName, password, persistent);
            if (num)
            {
                Cart result = CartManager.GetCurrentCart(visitorContext, storefront).Result;
                Tracker.Current.CheckForNull().Session.IdentifyAs("CommerceUser", userName);
                visitorContext.UserJustLoggedIn();

                #region modified code
                Contact contact = Tracker.Current.CheckForNull().Contact;
                ContactIdentifier contactIdentifier = contact.Identifiers.Where((ContactIdentifier c) => c.Source != null && c.Source.Equals("CommerceUser", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (contactIdentifier != null)
                {
                    string userNameFromContact = contactIdentifier.Identifier;

                    if (!userNameFromContact.Equals(Context.User.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        // Security alert - Tracker contact and Context user are out of sync
                        userNameFromContact = string.Empty;
                        Logout();
                    }
                }
                #endregion



                CartManager.MergeCarts(storefront.CurrentStorefront, visitorContext, customerId, result);
            }
            return num;
        }

        public virtual ManagerResponse<CreateUserResult, CommerceUser> RegisterUser(IStorefrontContext storefrontContext, string userName, string password, string email)
        {
            Assert.ArgumentNotNull(storefrontContext, "storefrontContext");
            Assert.ArgumentNotNullOrEmpty(userName, "userName");
            Assert.ArgumentNotNullOrEmpty(password, "password");
            CreateUserResult createUserResult;
            try
            {
                CreateUserRequest request = new CreateUserRequest(userName, password, email, storefrontContext.CurrentStorefront.ShopName);
                createUserResult = CustomerServiceProvider.CreateUser(request);
                if (!createUserResult.Success)
                {
                    Helpers.LogSystemMessages(createUserResult.SystemMessages, createUserResult);
                }
                else if (createUserResult.Success && createUserResult.CommerceUser == null && createUserResult.SystemMessages.Count == 0)
                {
                    createUserResult.Success = false;
                    createUserResult.SystemMessages.Add(new SystemMessage
                    {
                        Message = storefrontContext.GetSystemMessage("User Already Exists")
                    });
                }
            }
            catch (MembershipCreateUserException ex)
            {
                createUserResult = new CreateUserResult
                {
                    Success = false
                };
                createUserResult.SystemMessages.Add(new SystemMessage
                {
                    Message = ErrorCodeToString(storefrontContext, ex.StatusCode)
                });
            }
            catch (Exception)
            {
                createUserResult = new CreateUserResult
                {
                    Success = false
                };
                createUserResult.SystemMessages.Add(new SystemMessage
                {
                    Message = storefrontContext.GetSystemMessage("Unknown Membership Provider Error")
                });
            }
            CreateUserResult createUserResult2 = createUserResult;
            return new ManagerResponse<CreateUserResult, CommerceUser>(createUserResult2, createUserResult2.CommerceUser);
        }

        public virtual void Logout()
        {
            Tracker.Current.CheckForNull().EndVisit(clearVisitor: true);

            #region modified code
            //HttpContext.Current.Session.Abandon();

            var session = HttpContext.Current.Session;
            session.Clear();
            session.Abandon();
            session.RemoveAll();
            HttpContext.Current.Response.Cookies.Add(new HttpCookie("ASP.NET_SessionId", ""));
            #endregion

            AuthenticationManager.Logout();
        }

        public virtual ManagerResponse<GetPartiesResult, IEnumerable<Party>> GetCurrentCustomerParties(CommerceStorefront storefront, IVisitorContext visitorContext)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            GetPartiesResult serviceProviderResult = new GetPartiesResult
            {
                Success = false
            };
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(visitorContext.UserName);
            if (!user.ServiceProviderResult.Success || user.Result == null)
            {
                return new ManagerResponse<GetPartiesResult, IEnumerable<Party>>(serviceProviderResult, null);
            }
            return GetParties(storefront, new CommerceCustomer
            {
                ExternalId = user.Result.ExternalId
            });
        }

        public virtual ManagerResponse<GetPartiesResult, IEnumerable<Party>> GetParties(CommerceStorefront storefront, CommerceCustomer customer)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(customer, "user");
            GetPartiesRequest request = new GetPartiesRequest(customer);
            GetPartiesResult parties = CustomerServiceProvider.GetParties(request);
            object enumerable2;
            if (!parties.Success || parties.Parties == null)
            {
                IEnumerable<Party> enumerable = new List<Party>();
                enumerable2 = enumerable;
            }
            else
            {
                enumerable2 = parties.Parties.Cast<Party>();
            }
            IEnumerable<Party> result = (IEnumerable<Party>)enumerable2;
            Helpers.LogSystemMessages(parties.SystemMessages, parties);
            return new ManagerResponse<GetPartiesResult, IEnumerable<Party>>(parties, result);
        }

        public virtual ManagerResponse<UpdatePasswordResult, bool> ResetUserPassword(string emailAddress, string emailSubject, string emailBody)
        {
            Assert.ArgumentNotNullOrEmpty(emailAddress, "emailAddress");
            Assert.ArgumentNotNullOrEmpty(emailBody, "emailBody");
            bool result = false;
            UpdatePasswordResult updatePasswordResult = new UpdatePasswordResult
            {
                Success = true
            };
            try
            {
                ManagerResponse<GetUserResult, CommerceUser> user = GetUser(emailAddress);
                if (!user.ServiceProviderResult.Success || user.Result == null)
                {
                    updatePasswordResult.Success = false;
                    foreach (SystemMessage systemMessage in user.ServiceProviderResult.SystemMessages)
                    {
                        updatePasswordResult.SystemMessages.Add(systemMessage);
                    }
                }
                else
                {
                    string value = (HttpContext.Current != null) ? HttpContext.Current.Request.UserHostAddress : string.Empty;
                    string userNameByEmail = Membership.Provider.GetUserNameByEmail(user.Result.Email);
                    string value2 = Membership.Provider.ResetPassword(userNameByEmail, string.Empty);
                    MailUtility mailUtility = new MailUtility();
                    Hashtable placeholders = new Hashtable
                {
                    {
                        "[IPAddress]",
                        value
                    },
                    {
                        "[Password]",
                        value2
                    }
                };
                    MailTemplate model = ModelProvider.GetModel<MailTemplate>();
                    model.Initialize(emailSubject, emailBody, emailAddress, placeholders);
                    if (mailUtility.SendMail(model))
                    {
                        result = true;
                    }
                }
            }
            catch (Exception ex)
            {
                updatePasswordResult = new UpdatePasswordResult
                {
                    Success = false
                };
                updatePasswordResult.SystemMessages.Add(new SystemMessage
                {
                    Message = ex.Message
                });
            }
            return new ManagerResponse<UpdatePasswordResult, bool>(updatePasswordResult, result);
        }

        public virtual ManagerResponse<UpdatePasswordResult, bool> ChangeUserPassword(IVisitorContext visitorContext, string currentPassword, string newPassword)
        {
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            Assert.ArgumentNotNullOrEmpty(currentPassword, "currentPassword");
            Assert.ArgumentNotNullOrEmpty(newPassword, "newPassword");
            UpdatePasswordRequest request = new UpdatePasswordRequest(visitorContext.UserName, currentPassword, newPassword);
            UpdatePasswordResult updatePasswordResult = CustomerServiceProvider.UpdatePassword(request);
            if (!updatePasswordResult.Success && !updatePasswordResult.SystemMessages.Any())
            {
                string systemMessage = StorefrontContext.GetSystemMessage("Change Password Error");
                updatePasswordResult.SystemMessages.Add(new SystemMessage
                {
                    Message = systemMessage
                });
            }
            if (!updatePasswordResult.Success)
            {
                Helpers.LogSystemMessages(updatePasswordResult.SystemMessages, updatePasswordResult);
            }
            UpdatePasswordResult updatePasswordResult2 = updatePasswordResult;
            return new ManagerResponse<UpdatePasswordResult, bool>(updatePasswordResult2, updatePasswordResult2.Success);
        }

        public virtual ManagerResponse<AddPartiesResult, bool> AddAddress(CommerceStorefront storefront, IVisitorContext visitorContext, CommerceParty address)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            Assert.ArgumentNotNull(address, "address");
            AddPartiesResult addPartiesResult = new AddPartiesResult
            {
                Success = false
            };
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(visitorContext.UserName);
            if (!user.ServiceProviderResult.Success || user.Result == null)
            {
                addPartiesResult.SystemMessages.ToList().AddRange(user.ServiceProviderResult.SystemMessages);
                return new ManagerResponse<AddPartiesResult, bool>(addPartiesResult, result: false);
            }
            AddPartiesRequest request = new AddPartiesRequest(new CommerceCustomer
            {
                ExternalId = user.Result.ExternalId
            }, new List<Party>
        {
            address
        });
            addPartiesResult = CustomerServiceProvider.AddParties(request);
            Helpers.LogSystemMessages(addPartiesResult.SystemMessages, addPartiesResult);
            AddPartiesResult addPartiesResult2 = addPartiesResult;
            return new ManagerResponse<AddPartiesResult, bool>(addPartiesResult2, addPartiesResult2.Success);
        }

        public virtual ManagerResponse<CustomerResult, bool> UpdateAddress(CommerceStorefront storefront, IVisitorContext visitorContext, CommerceParty address)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            Assert.ArgumentNotNull(address, "address");
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(visitorContext.UserName);
            if (!user.ServiceProviderResult.Success || user.Result == null)
            {
                CustomerResult obj = new CustomerResult
                {
                    Success = false
                };
                obj.SystemMessages.ToList().AddRange(user.ServiceProviderResult.SystemMessages);
                return new ManagerResponse<CustomerResult, bool>(obj, result: false);
            }
            UpdatePartiesRequest request = new UpdatePartiesRequest(new CommerceCustomer
            {
                ExternalId = user.Result.ExternalId
            }, new List<Party>
        {
            address
        });
            CustomerResult customerResult = CustomerServiceProvider.UpdateParties(request);
            if (!customerResult.Success)
            {
                Helpers.LogSystemMessages(customerResult.SystemMessages, customerResult);
            }
            CustomerResult customerResult2 = customerResult;
            return new ManagerResponse<CustomerResult, bool>(customerResult2, customerResult2.Success);
        }

        public virtual ManagerResponse<GetPartiesResult, IEnumerable<CommerceParty>> GetCurrentCustomerAddresses(CommerceStorefront storefront, IVisitorContext visitorContext)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            GetPartiesResult getPartiesResult = new GetPartiesResult
            {
                Success = false
            };
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(visitorContext.UserName);
            if (!user.ServiceProviderResult.Success || user.Result == null)
            {
                getPartiesResult.SystemMessages.ToList().AddRange(user.ServiceProviderResult.SystemMessages);
                return new ManagerResponse<GetPartiesResult, IEnumerable<CommerceParty>>(getPartiesResult, null);
            }
            GetPartiesRequest request = new GetPartiesRequest(new CommerceCustomer
            {
                ExternalId = user.Result.ExternalId
            });
            getPartiesResult = CustomerServiceProvider.GetParties(request);
            object enumerable2;
            if (!getPartiesResult.Success || getPartiesResult.Parties == null)
            {
                IEnumerable<CommerceParty> enumerable = new List<CommerceParty>();
                enumerable2 = enumerable;
            }
            else
            {
                enumerable2 = getPartiesResult.Parties.Cast<CommerceParty>();
            }
            IEnumerable<CommerceParty> result = (IEnumerable<CommerceParty>)enumerable2;
            Helpers.LogSystemMessages(getPartiesResult.SystemMessages, getPartiesResult);
            return new ManagerResponse<GetPartiesResult, IEnumerable<CommerceParty>>(getPartiesResult, result);
        }

        public virtual ManagerResponse<CustomerResult, bool> DeleteAddress(CommerceStorefront storefront, IVisitorContext visitorContext, string addressId)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            Assert.ArgumentNotNullOrEmpty(addressId, "addressId");
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(visitorContext.UserName);
            if (!user.ServiceProviderResult.Success || user.Result == null)
            {
                CustomerResult obj = new CustomerResult
                {
                    Success = false
                };
                obj.SystemMessages.ToList().AddRange(user.ServiceProviderResult.SystemMessages);
                return new ManagerResponse<CustomerResult, bool>(obj, result: false);
            }
            CommerceCustomer user2 = new CommerceCustomer
            {
                ExternalId = user.Result.ExternalId
            };
            List<Party> parties = new List<Party>
        {
            new Party
            {
                ExternalId = addressId
            }
        };
            return RemoveParties(storefront, user2, parties);
        }

        public virtual ManagerResponse<CustomerResult, bool> RemoveParties(CommerceStorefront storefront, CommerceCustomer user, List<Party> parties)
        {
            Assert.ArgumentNotNull(storefront, "storefront");
            Assert.ArgumentNotNull(user, "user");
            Assert.ArgumentNotNull(parties, "parties");
            RemovePartiesRequest request = new RemovePartiesRequest(user, parties);
            CustomerResult customerResult = CustomerServiceProvider.RemoveParties(request);
            if (!customerResult.Success)
            {
                Helpers.LogSystemMessages(customerResult.SystemMessages, customerResult);
            }
            CustomerResult customerResult2 = customerResult;
            return new ManagerResponse<CustomerResult, bool>(customerResult2, customerResult2.Success);
        }

        public virtual ManagerResponse<UpdateUserResult, CommerceUser> UpdateUser(IVisitorContext visitorContext, string firstName, string lastName, string phoneNumber, string emailAddress)
        {
            Assert.ArgumentNotNull(visitorContext, "visitorContext");
            Assert.ArgumentNotNullOrEmpty(emailAddress, "emailAddress");
            string userName = visitorContext.UserName;
            UpdateUserResult updateUserResult = new UpdateUserResult
            {
                Success = false
            };
            ManagerResponse<GetUserResult, CommerceUser> user = GetUser(userName);
            CommerceUser result = user.Result;
            if (result != null)
            {
                result.FirstName = firstName;
                result.LastName = lastName;
                result.Email = emailAddress;
                result.SetPropertyValue("Phone", phoneNumber);
                try
                {
                    UpdateUserRequest request = new UpdateUserRequest(result);
                    updateUserResult = CustomerServiceProvider.UpdateUser(request);
                }
                catch (Exception ex)
                {
                    updateUserResult = new UpdateUserResult
                    {
                        Success = false
                    };
                    updateUserResult.SystemMessages.Add(new SystemMessage
                    {
                        Message = ex.Message + "/" + ex.StackTrace
                    });
                }
            }
            else
            {
                updateUserResult.Success = false;
                foreach (SystemMessage systemMessage in user.ServiceProviderResult.SystemMessages)
                {
                    updateUserResult.SystemMessages.Add(systemMessage);
                }
            }
            Helpers.LogSystemMessages(updateUserResult.SystemMessages, updateUserResult);
            UpdateUserResult updateUserResult2 = updateUserResult;
            return new ManagerResponse<UpdateUserResult, CommerceUser>(updateUserResult2, updateUserResult2.CommerceUser);
        }

        protected virtual string ErrorCodeToString(IStorefrontContext storefrontContext, MembershipCreateStatus createStatus)
        {
            string messageKey = "Unknown Membership Provider Error";
            switch (createStatus)
            {
                case MembershipCreateStatus.DuplicateUserName:
                    messageKey = "User Already Exists";
                    break;
                case MembershipCreateStatus.DuplicateEmail:
                    messageKey = "User Name For Email Exists";
                    break;
                case MembershipCreateStatus.InvalidPassword:
                    messageKey = "Invalid Password Error";
                    break;
                case MembershipCreateStatus.InvalidEmail:
                    messageKey = "Invalid Email Error";
                    break;
                case MembershipCreateStatus.InvalidAnswer:
                    messageKey = "Password Retrieval Answer Invalid";
                    break;
                case MembershipCreateStatus.InvalidQuestion:
                    messageKey = "Password Retrieval Question Invalid";
                    break;
                case MembershipCreateStatus.InvalidUserName:
                    messageKey = "User Name Invalid";
                    break;
                case MembershipCreateStatus.ProviderError:
                    messageKey = "Authentication Provider Error";
                    break;
                case MembershipCreateStatus.UserRejected:
                    messageKey = "User Rejected Error";
                    break;
            }
            return storefrontContext.GetSystemMessage(messageKey);
        }
    }
}
