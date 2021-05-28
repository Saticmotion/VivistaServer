﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace VivistaServer
{
	public class VideoController
	{
		private const int indexCountDefault = 10;
		private const string baseFilePath = @"C:\VivistaServerData\";

		public class Video
		{
			public Guid id;
			public int userid;
			public string username;
			public DateTime timestamp;
			public int downloadsize;

			public string title;
			public string description;
			public int length;
		}

		public class VideoResponse
		{
			public int totalcount;
			public int page;
			public int count;
			public IEnumerable<Video> videos;
		}

		public class Meta
		{
			public Guid guid;
			public string title;
			public string description;
			public int length;
		}

		private enum IndexTab
		{
			New,
			Popular,
			MostWatched
		}


		[Route("GET", "/api/videos")]
		[Route("GET", "/api/v1/videos")]
		private static async Task VideosGetApi(HttpContext context)
		{
			var args = context.Request.Query;

			string author = args["author"].ToString().ToLowerInvariant().Trim();
			int? userid = null;
			DateTime? uploadDate;

			if (!Int32.TryParse(args["offset"].ToString(), out int offset) || offset < 0)
			{
				offset = 0;
			}

			if (!Int32.TryParse(args["count"].ToString(), out int count) || count > 100 || count < 0)
			{
				count = indexCountDefault;
			}

			using var connection = Database.OpenNewConnection();

			//TODO(Simon): Fuzzy search for username
			if (!String.IsNullOrEmpty(author))
			{
				userid = await UserController.UserIdFromUsername(author, connection);
			}

			if (!Int32.TryParse(args["agedays"].ToString(), out int daysOld) || daysOld < 0)
			{
				uploadDate = null;
			}
			else
			{
				uploadDate = DateTime.UtcNow.AddDays(-daysOld);
			}

			var videos = new VideoResponse();

			try
			{
				//TODO(Simon): There might be a faster way to get the count, while also executing just 1 query: add "count(*) OVER() AS total_count" to query
				videos.totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=@userid)
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)", new { userid, uploadDate });
				videos.videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=@userid)
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)
								order by v.timestamp desc
								limit @count
								offset @offset", new { userid, uploadDate, count, offset });

				videos.count = videos.videos.AsList().Count;
				videos.page = videos.totalcount > 0 ? offset / videos.totalcount + 1 : 1;
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(videos));
			}
			catch (Exception e)
			{
				await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
			}
		}

		[Route("GET", "/api/video")]
		[Route("GET", "/api/v1/video")]
		private static async Task VideoGetApi(HttpContext context)
		{
			var args = context.Request.Query;

			if (!Guid.TryParse(args["videoid"], out var videoid))
			{
				await CommonController.Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();

			Video video;
			try
			{
				video = await GetVideo(videoid, connection);

				if (video == null)
				{
					await CommonController.Write404(context);
					return;
				}
			}
			catch (Exception e)
			{
				await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			var videoPath = $"{baseFilePath}{video.id}\\main.mp4";

			if (File.Exists(videoPath))
			{
				context.Response.ContentType = "video/mp4";
				try
				{
					await context.Response.SendFileAsync(videoPath);
				}
				catch (Exception e)
				{
					await CommonController.WriteError(context, "Something went wrong while sending this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/api/video")]
		[Route("POST", "/api/v1/video")]
		private static async Task VideoPostApi(HttpContext context)
		{
			context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = null;

			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			var user = await UserSessions.GetLoggedInUser(context);

			if (user != null)
			{
				var guid = new Guid(form["uuid"]);
				string basePath = Path.Combine(baseFilePath, guid.ToString());
				string videoPath = Path.Combine(basePath, "main.mp4");
				string metaPath = Path.Combine(basePath, "meta.json");
				string thumbPath = Path.Combine(basePath, "thumb.jpg");

				try
				{
					Directory.CreateDirectory(basePath);
					using (var videoStream = new FileStream(videoPath, FileMode.OpenOrCreate))
					using (var metaStream = new FileStream(metaPath, FileMode.OpenOrCreate))
					using (var thumbStream = new FileStream(thumbPath, FileMode.OpenOrCreate))
					{
						var videoCopyOp = form.Files["video"].CopyToAsync(videoStream);
						var metaCopyOp = form.Files["meta"].CopyToAsync(metaStream);
						var thumbCopyOp = form.Files["thumb"].CopyToAsync(thumbStream);
						await Task.WhenAll(videoCopyOp, metaCopyOp, thumbCopyOp);
					}

					//TODO(Simon): Move all the file-reading and database code somewhere else. So that users don't have to reupload if database insert fails
					var meta = await Task.Run(() => ReadMetaFile(metaPath));

					if (await UserOwnsVideo(guid, user.userid, connection))
					{
						var timestamp = DateTime.UtcNow;
						await connection.ExecuteAsync(@"update videos set (title, description, length, timestamp)
													= (@title, @description, @length, @timestamp)
													where id = @guid",
													new { guid, meta.title, meta.description, meta.length, timestamp });
					}
					else
					{
						await connection.ExecuteAsync(@"insert into videos (id, userid, title, description, length)
													values (@guid, @userid, @title, @description, @length)",
													new { guid, user.userid, meta.title, meta.description, meta.length });
					}

					await context.Response.WriteAsync("{}");
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await CommonController.WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await CommonController.WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
		}

		[Route("GET", "/api/meta")]
		[Route("GET", "/api/v1/meta")]
		private static async Task MetaGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string id = args["videoid"].ToString();

			if (Guid.TryParse(id, out _))
			{
				string filename = Path.Combine(baseFilePath, id, "meta.json");
				await CommonController.WriteFile(context, filename, "application/json", "meta.json");
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/thumbnail")]
		[Route("GET", "/api/v1/thumbnail")]
		private static async Task ThumbnailGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string id = args["id"];

			if (Guid.TryParse(id, out _))
			{
				string filename = Path.Combine(baseFilePath, id, "thumb.jpg");
				await CommonController.WriteFile(context, filename, "image/jpg", "thumb.jpg");
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/extra")]
		[Route("GET", "/api/v1/extra")]
		private static async Task ExtraGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string videoId = args["videoid"];
			string extraId = args["extraid"];

			if (String.IsNullOrEmpty(videoId) || String.IsNullOrEmpty(extraId))
			{
				await CommonController.Write404(context);
				return;
			}

			if (Guid.TryParse(videoId, out _) && Guid.TryParse(extraId, out _))
			{
				string filename = Path.Combine(baseFilePath, videoId, "extra", extraId);
				await CommonController.WriteFile(context, filename, "application/octet-stream", "");
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/extras")]
		[Route("GET", "/api/v1/extras")]
		private static async Task ExtrasGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string idstring = args["videoid"];

			if (String.IsNullOrEmpty(idstring))
			{
				await CommonController.Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();

			if (Guid.TryParse(idstring, out var videoId))
			{
				try
				{
					var ids = await connection.QueryAsync<Guid>(@"select guid from extra_files where video_id = @videoId", new { videoId });
					var stringIds = new List<string>();
					foreach (var id in ids) { stringIds.Add(id.ToString().Replace("-", "")); }
					await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(stringIds));
				}
				catch (Exception e)
				{
					await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				}
			}
		}

		[Route("POST", "/api/extras")]
		[Route("POST", "/api/v1/extras")]
		private static async Task ExtrasPostApi(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string videoGuid = form["videoguid"];
			string rawExtraGuids = form["extraguids"];
			var extraGuids = rawExtraGuids.Split(',');

			if (await UserSessions.GetLoggedInUser(context) != null)
			{
				string basePath = Path.Combine(baseFilePath, videoGuid);
				string extraPath = Path.Combine(basePath, "extra");
				try
				{
					Directory.CreateDirectory(extraPath);

					foreach (var file in form.Files)
					{
						using (var stream = new FileStream(Path.Combine(extraPath, file.Name), FileMode.OpenOrCreate))
						{
							await file.CopyToAsync(stream);
						}
					}

					var clearTask = connection.ExecuteAsync("delete from extra_files where video_id = @videoGuid::uuid", new { videoGuid });

					var param = new[]
					{
						new { video_id = "", guid = "" }
					}.ToList();

					param.Clear();

					foreach (var id in extraGuids)
					{
						param.Add(new { video_id = videoGuid, guid = id });
					}

					var downloadSizeTask = Task.Run(() => GetDirectorySize(new DirectoryInfo(basePath)));

					await clearTask;
					await connection.ExecuteAsync("insert into extra_files (video_id, guid) values (@video_id::uuid, @guid::uuid)", param);
					long downloadSize = await downloadSizeTask;
					await connection.ExecuteAsync("update videos set (downloadsize) = (@downloadSize) where id = @videoGuid::uuid", new { videoGuid, downloadsize = downloadSize });

					await context.Response.WriteAsync("{}");
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await CommonController.WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await CommonController.WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
		}

		[Route("GET", "/")]
		private static async Task IndexGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);
			var tabString = context.Request.Query["tab"].ToString();
			var tab = tabString switch
			{
				"new" => IndexTab.New,
				"popular" => IndexTab.Popular,
				_ => IndexTab.MostWatched
			};

			int count = 10;
			int offset = 0;

			using var connection = Database.OpenNewConnection();

			IEnumerable<Video> videos = null;
			if (tab == IndexTab.New)
			{
				videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								order by v.timestamp desc
								limit @count
								offset @offset", new {count, offset});
			}
			else if (tab == IndexTab.MostWatched)
			{
				videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								order by v.views desc
								limit @count
								offset @offset", new { count, offset });
			}
			else if (tab == IndexTab.Popular)
			{
				videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								order by v.views desc
								limit @count
								offset @offset", new { count, offset });
			}

			var templateContext = new TemplateContext(new { videos, tab = tab.ToString() });

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\index.liquid", templateContext));
		}

		[Route("GET", "/video")]
		private static async Task VideoGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			if (Guid.TryParse(context.Request.Query["id"], out var videoId))
			{
				using var connection = Database.OpenNewConnection();
				var video = await GetVideo(videoId, connection);

				if (video != null)
				{
					var templateContext = new TemplateContext(new { video });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\video.liquid", templateContext));
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/my_videos")]
		private static async Task MyVideosGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				const int countPerPage = 20;
				int page = 1;
				if (context.Request.Query.ContainsKey("page"))
				{
					Int32.TryParse(context.Request.Query["page"], out page);
				}

				int offset = (page - 1) * countPerPage;

				var numVideos = await NumVideosForUser(user.userid, connection);
				var VideosTask = VideosForUser(user.userid, countPerPage, offset, connection);

				var pagination = new Pagination(numVideos, countPerPage, offset);

				var templateContext = new TemplateContext(new { videos = await VideosTask, pagination });

				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\myVideos.liquid", templateContext));
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/user")]
		private static async Task UserGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var username = context.Request.Query["name"].ToString();

			if (!String.IsNullOrEmpty(username))
			{
				using var connection = Database.OpenNewConnection();
				var user = await UserController.UserFromUsername(username, connection);

				if (user != null)
				{
					const int countPerPage = 20;
					int page = 1;
					if (context.Request.Query.ContainsKey("page"))
					{
						Int32.TryParse(context.Request.Query["page"], out page);
					}

					int offset = (page - 1) * countPerPage;
				
					var numVideos = await NumPublicVideosForUser(user.userid, connection);
					var VideosTask = PublicVideosForUser(user.userid, countPerPage, offset, connection);

					var pagination = new Pagination(numVideos, countPerPage, offset);

					var templateContext = new TemplateContext(new {videos = await VideosTask, user, pagination});

					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\user.liquid", templateContext));
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}


		[Route("GET", "/delete_video")]
		private static async Task DeleteVideoGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && Guid.TryParse(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection);
				if (UserOwnsVideo(video, user.userid))
				{
					var templateContext = new TemplateContext(new {video});
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\deleteVideoConfirm.liquid", templateContext));
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}

		}

		[Route("GET", "/delete_video_confirm")]
		private static async Task DeleteVideoConfirmGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && Guid.TryParse(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection);
				if (UserOwnsVideo(video, user.userid))
				{
					await DeleteVideo(video.id, connection);

					context.Response.Redirect("/my_videos");
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}


		public static async Task<IEnumerable<Video>> VideosForUser(int userid, int count, int offset, NpgsqlConnection connection)
		{
			try
			{
				var videos = await connection.QueryAsync<Video>(@"select id, timestamp, downloadsize, title, description, length from videos
																where userid=@userid
																order by timestamp desc
																limit @count
																offset @offset", new { userid, count, offset });
				
				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		//TODO(Simon): Filter on public videos
		public static async Task<IEnumerable<Video>> PublicVideosForUser(int userid, int count, int offset, NpgsqlConnection connection)
		{
			try
			{
				var videos = await connection.QueryAsync<Video>(@"select id, timestamp, downloadsize, title, description, length from videos
																where userid=@userid
																order by timestamp desc
																limit @count
																offset @offset", new { userid, count, offset });

				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		private static async Task<int> NumVideosForUser(int userid, NpgsqlConnection connection)
		{
			try
			{
				int totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos
																		where userid=@userid", new { userid });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		//TODO(Simon): Filter on public videos
		public static async Task<int> NumPublicVideosForUser(int userid, NpgsqlConnection connection)
		{
			try
			{
				int totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos
																		where userid=@userid", new { userid });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		private static async Task<bool> UserOwnsVideo(Guid guid, int userId, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>("select count(*) from videos where id=@guid and userid=@userid", new { guid, userId });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static bool UserOwnsVideo(Video video, int userId)
		{
			return video?.userid == userId;
		}

		private static async Task<bool> VideoExists(Guid guid, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>("select count(*) from videos where id=$1", new { guid });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static async Task<Video> GetVideo(Guid videoid, NpgsqlConnection connection)
		{
			try
			{
				var video = await connection.QuerySingleAsync<Video>(@"select v.id, v.userid, u.username, v.title, v.description, v.timestamp, v.downloadsize from videos v
													inner join users u on v.userid = u.userid
													where v.id=@videoid::uuid", new {videoid});
				return video;
			}
			catch (Exception e)
			{
				return null;
			}
		}

		private static async Task<bool> DeleteVideo(Guid videoid, NpgsqlConnection connection)
		{
			try
			{
				var result = await connection.ExecuteAsync(@"delete from videos
															where id=@videoid::uuid", new { videoid });
				return result > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static Meta ReadMetaFile(string path)
		{
			var raw = File.ReadAllText(path).AsSpan();
			var meta = new Meta();

			try
			{
				_ = GetNextMetaValue(ref raw);
				meta.guid = Guid.Parse((ReadOnlySpan<char>) GetNextMetaValue(ref raw));
				meta.title = GetNextMetaValue(ref raw).ToString();
				meta.description = GetNextMetaValue(ref raw).ToString();
				meta.length = (int)float.Parse(GetNextMetaValue(ref raw));

				return meta;
			}
			catch
			{
				return null;
			}
		}

		private static ReadOnlySpan<char> GetNextMetaValue(ref ReadOnlySpan<char> text)
		{
			int start = text.IndexOf(':') + 1;
			int end = text.IndexOf('\n');
			int length = end - start - 1;

			var line = text.Slice(start, length);
			text = text.Slice(end + 1);

			return line;
		}

		private static long GetDirectorySize(DirectoryInfo d)
		{
			long size = 0;

			var files = d.GetFiles("*.*", SearchOption.AllDirectories);
			foreach (var file in files)
			{
				size += file.Length;
			}

			var subDirs = d.GetDirectories();
			foreach (var dir in subDirs)
			{
				size += GetDirectorySize(dir);
			}
			return size;
		}
	}
}