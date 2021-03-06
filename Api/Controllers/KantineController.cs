﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Api.ImageSearch;
using Api.Models.Slack;
using Api.Models.Slack.Blocks;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [ApiController]
    public class KantineController : ControllerBase
    {
        private readonly IHelsedirMenuService _menuService;
        private readonly IImageSearcher _imageSearcher;
        private readonly IHttpClientFactory _httpClientFactory;

        public KantineController(
            IHelsedirMenuService menuService,
            IImageSearcher imageSearcher,
            IHttpClientFactory httpClientFactory)
        {
            _menuService = menuService;
            _imageSearcher = imageSearcher;
            _httpClientFactory = httpClientFactory;
        }

        [Route("kantine")]
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var message = new SlackMessage
            {
                blocks = await CreateSlackMessage(false),
            };

            return Ok(message);
        }

        //[Route("kantine/image")]
        //[HttpGet]
        //public async Task<IActionResult> Get(string meal)
        //{
        //    var url = await _imageSearcher.SearchForMeal(meal);

        //    if (url == null) return NotFound("No image found :'(");

        //    return Ok(url);
        //}

        [Route("kantine/post-to-url")]
        [HttpGet]
        public async Task<IActionResult> PostToUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://hooks.slack.com/services/"))
            {
                return BadRequest("Requires URL. And must start with https://hooks.slack.com/services/.");
            }

            var message = new SlackMessage
            {
                blocks = await CreateSlackMessage(allInOneNastyBlob: false),
                response_type = "ephemeral"
            };

            var client = _httpClientFactory.CreateClient();

            var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                IgnoreNullValues = true
            });

            var response = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

            return Ok(await response.Content.ReadAsStringAsync());
        }

        [Route("kantine/slack")]
        [HttpPost]
        public async Task<IActionResult> SlackDirectResponse([FromForm] SlackPost post)
        {
            SlackMessage message = null;
            if (post?.text != null)
            {
                if (post.IsCommand("help") || post.IsCommand("hjelp"))
                {
                    var text = "*Kommandoer:\n" +
                               "*announce* - Gir output til hele kanalen\n" +
                               "*all* - Viser menyen for hele uka\n" +
                               "*help* - Denne hjelpen.\n" +
                               "\n" +
                               "https://github.com/Hoaas/Vitaminveien4Menu4Slack";
                    message = new SlackMessage
                    {
                        blocks = CreateDefaultSectionText(text)
                    };
                }
                else if (post.IsCommand("all") || post.IsCommand("alt"))
                {
                    message = new SlackMessage
                    {
                        blocks = await CreateSlackMessage(allInOneNastyBlob: true)
                    };
                }
            }

            if (message == null)
            {
                message = new SlackMessage
                {
                    blocks = await CreateSlackMessage(allInOneNastyBlob: false),
                };
            }

            if (!post.IsCommand("announce"))
            {
                message.response_type = "ephemeral";
            }

            return Ok(message);
        }

        private async Task<List<object>> CreateSlackMessage(bool allInOneNastyBlob)
        {
            if (allInOneNastyBlob)
            {
                try
                {
                    var allMenu = await _menuService.FetchEntireMenuAsText();
                    return CreateDefaultSectionText(allMenu);
                }
                catch (WorkplaceNotWorkingException)
                {
                    return CreateDefaultSectionText("https://workplace.izy.as/ er nede?");
                }
            }
            try
            {
                var menu = await _menuService.FetchWeeklyMenu();

                if (menu.Count > 0)
                {
                    return await CreateMessageForSpecificDay(menu);
                }

                // Fallback 
                var dailyMenu = await _menuService.FetchDailyMenu();

                return await CreateMessageForDailyMenu(dailyMenu);
            }
            catch (WorkplaceNotWorkingException)
            {
                return CreateDefaultSectionText("https://workplace.izy.as/ er nede?");
            }
        }

        private async Task<List<object>> CreateMessageForDailyMenu(IEnumerable<string> dailyMenu)
        {
            var blocks = CreateDefaultSectionText("*Meny for i dag*");
            blocks.AddRange(await CreateSlackAttachment(dailyMenu));
            return blocks;
        }

        /// <summary>
        /// Oversetter fra engelsk navn på dag til norsk
        /// </summary>
        /// <remarks>
        /// Workaround for workaround.
        /// Docker image som benyttes er satt opp i Culture Invariant Mode
        /// https://github.com/dotnet/dotnet-docker/issues/1110
        ///
        /// Dette imaget brukes igjen fordi det var sertifikat-issues med vanlig dotnet image januar 2021.
        /// </remarks>
        /// 
        /// <param name="day"></param>
        /// <returns></returns>
        private string Norwegify(string day)
        {
            day = day.ToLower().Trim();

            return day switch
            {
                "monday" => "mandag",
                "tuesday" => "tirsdag",
                "wednesday" => "onsdag",
                "thursday" => "torsdag",
                "friday" => "fredag",
                "saturday" => "lørdag",
                "sunday" => "søndag",
                _ => day
            };
        }

        private async Task<List<object>> CreateMessageForSpecificDay(Dictionary<string, List<string>> menu)
        {
            var today = DateTime.Now.ToString("dddd", CultureInfo.GetCultureInfo("nb-NO"));
            today = Norwegify(today);

            var meals = (
                from menuKey in menu.Keys
                where menuKey.ToLower().Contains(today.ToLower())
                select menu[menuKey]).FirstOrDefault();

            if (meals == null)
            {
                return new List<object>
                {
                    new SectionBlock
                    {
                        Text = new TextBlock
                        {
                            Text = $"Finner ikke meny for {today} (eller andre dager for den saksskyld)"
                        }
                    }
                };
            }

            var blocks = CreateDefaultSectionText($"*Meny for {today}*");
            blocks.AddRange(await CreateSlackAttachment(meals));
            return blocks;
        }

        private static List<object> CreateDefaultSectionText(string text)
        {
            return new List<object>
            {
                new SectionBlock
                {
                    Text = new TextBlock {Text = text}
                }
            };
        }

        private async Task<IEnumerable<object>> CreateSlackAttachment(IEnumerable<string> meals)
        {
            var blocks = new List<object>();
            foreach (var meal in meals)
            {
                var block = new SectionBlock
                {
                    Text = new TextBlock(),
                    Accessory = new AccessoryBlock()
                };
                //var dayAndMeal = meal.Contains(":")
                //    ? meal.Split(":")
                //    : meal.Split(' ', 2);

                //if (dayAndMeal.Length != 2) continue;

                //var dishName = dayAndMeal[1].Trim();

                var dishName = meal;

                if (string.IsNullOrWhiteSpace(dishName))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(dishName))
                {
                    block.Text.Text = $"*{dishName}*";
                    var url = await _imageSearcher.SearchForMeal(dishName);
                    if (url != null)
                    {
                        block.Accessory.AltText = dishName;
                        block.Accessory.ImageUrl = url;
                    }
                }
                blocks.Add(block);
            }

            return blocks;
        }
    }
}
