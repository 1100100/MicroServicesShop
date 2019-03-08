﻿using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper.Extensions;
using Ocelot.JwtAuthorize;
using Shop.Common.Identity;
using Shop.IIdentity;
using Utils.Encrypt;

namespace Shop.IdentityService
{
    public class IdentityService : IIdentityService
    {
        private IDapper Dapper { get; }
        private ITokenBuilder TokenBuilder { get; }

        public IdentityService(IDapper dapper, ITokenBuilder tokenBuilder)
        {
            Dapper = dapper;
            TokenBuilder = tokenBuilder;
        }

        public async Task<(bool Succeed, string ErrorMessage)> Register(RegisterView userInfo)
        {
            if (await Dapper.ExecuteScalarAsync<int>("select count(1) from Users where email=@Email",
                    new { userInfo.Email }) > 0)
                return (false, "Email address already exists");
            var ok = await Dapper.ExecuteAsync("insert into Users(email,password,nickname) values(@Email,@Password,@NickName);", new
            {
                userInfo.Email,
                Password = BCryptor.Encrypt(userInfo.Password),
                userInfo.NickName
            }) > 0;
            return ok ? (true, "") : (false, "Registration failed");
        }

        public async Task<(bool Succeed, string ErrorMessage, AuthResult Result)> Login(LoginView login)
        {
            var user = await Dapper.QueryFirstOrDefaultAsync("select email,password,nickname from Users where email=@Email;",
                new { login.Email });
            if (user == null)
                return (false, "Email does not exist!", null);
            if (!BCryptor.Verify(login.Password, user.password))
                return (false, "The password is incorrect!", null);
            var claims = new Claim[] {
                new Claim(ClaimTypes.Name, user.nickname),
                new Claim(ClaimTypes.NameIdentifier, login.Email),
                new Claim(ClaimTypes.Role, "user"),
            };
            var token = TokenBuilder.BuildJwtToken(claims);
            Console.WriteLine($"---------------------->{token.TokenValue}");
            return (true, "", new AuthResult
            {
                Token = token.TokenValue,
                Expire = token.Expires
            });
        }
    }
}
